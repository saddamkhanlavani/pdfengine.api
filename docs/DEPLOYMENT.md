# Deploying PDFEngine

Everything in this document was verified against the container in this repository, not
inferred from how .NET services usually work. Where a number appears it was measured, and
the measurement is named.

---

## 1. Secrets — the service fails closed

`StartupConfigValidator` refuses to start when a value committed to this repository reaches
any environment other than `Development`. That includes the JWT signing key, the Stripe
placeholder, the MinIO credentials and the database password.

This is deliberate and it is not a warning. A service that boots with a publicly known
signing key issues tokens **anyone with the source can forge for any tenant, including
admin**, and nothing about it looks wrong from outside. Verified: a Production start with
the committed key exits with

```
CRITICAL CONFIGURATION ERROR: Jwt:Key, Stripe:SecretKey, ConnectionStrings:DefaultConnection
still hold values committed to this repository, and the environment is 'Production'…
```

Generate a signing key with at least 32 bytes of real entropy:

```bash
head -c 48 /dev/urandom | base64
```

---

## 2. Environment matrix

Every default in `appsettings.json` points at `127.0.0.1`, which inside a container is the
container itself. Each of these must be set. Double underscore is the .NET nesting
separator.

| Variable | Required | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | **yes** | `Production`. Anything but `Development` enables the secret check above. |
| `ConnectionStrings__DefaultConnection` | **yes** | `Host=…;Database=…;Username=…;Password=…;Timeout=5;Command Timeout=15`. `Timeout=5` so an unreachable database is discovered in five seconds, not thirty — measured under a network partition. |
| `Redis__ConnectionString` | **yes** | `host:6379,abortConnect=false` |
| `Jwt__Key` | **yes** | ≥32 bytes, real entropy. Rotating it invalidates every issued token. |
| `Jwt__Issuer`, `Jwt__Audience` | **yes** | Validated at boot. |
| `Cors__AllowedOrigins__0` | if a browser calls the API | The dashboard's origin, e.g. `https://app.example.com`. Unset means no browser client can call the API; the engine logs its CORS posture at boot so this is visible in the log rather than only in a browser console. |
| `AWS__ServiceURL` | if using object storage | Empty disables S3 entirely and the engine falls back to local storage. |
| `AWS__AccessKey`, `AWS__SecretKey`, `AWS__BucketName` | with `AWS__ServiceURL` | |
| `Database__MigrateOnStartup` | no | Default `false`. See below. |
| `PdfEngine__MaxConcurrentRenders` | no | Default 2. Scales with memory — see §5. |

`appsettings.Production.json` holds no secrets and no hostnames. It exists so the safe
posture (detailed errors off, migrate-on-start off, no CORS origins) is the default rather
than something each deployment has to remember.

---

## 3. Database migrations

Nothing applied migrations before release preparation: a fresh database had **no schema**,
and the failure appeared on the first request that touched a table rather than at deploy.
There are now two supported ways, and the default is neither, so nothing silently rewrites
a schema.

**Deployment pipeline or Kubernetes init container (recommended):**

```bash
dotnet PdfEngine.API.dll --migrate-and-exit
```

Applies pending migrations, logs what it applied, exits 0. Exits 1 if migration fails, so a
broken schema change stops the rollout instead of half-migrating under live traffic. Run it
once per deploy, before any instance serves.

**Single instance or compose:** set `Database__MigrateOnStartup=true`. Genuinely unsafe with
several replicas starting at once — they race the same schema change — which is why it is
opt-in.

---

## 3a. First run — the whole sequence, executed

Run end-to-end on 2026-08-20 against an empty database and a Production container. Every
step below produced the result shown.

```bash
# 1. Schema. 22 migrations, 0 -> 32 tables, exit 0.
docker run --rm \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__DefaultConnection="…" \
  -e Redis__ConnectionString="…" -e Jwt__Key="…" \
  pdfengine:local dotnet PdfEngine.API.dll --migrate-and-exit

# 2. Start the service. Live in ~20s.
#    /health/live 200, /health/ready reflects the dependencies.

# 3. Create the first account. There is no seeded tenant, user or API key in
#    Production — the development seed is gated to Development, and a dev key is
#    correctly refused with 401.
curl -X POST https://…/api/v1/auth/register -H 'Content-Type: application/json' \
  -d '{"email":"…","password":"…","companyName":"…","fullName":"…"}'

# 4. Log in, then mint an API key.
curl -X POST https://…/api/v1/auth/login -d '{"email":"…","password":"…"}'
curl -X POST https://…/api/v1/account/keys -H "Authorization: Bearer $TOKEN" \
  -d '{"name":"first-key"}'
# -> { "id", "name", "key": "pk_live_…", "created", "environment", "status" }
#    The plaintext key is returned ONCE. Store it now.

# 5. Render.
curl -X POST https://…/api/v1/pdf/generate -H "X-Api-Key: pk_live_…" \
  -d '{"documentName":"first","documentType":4,"html":"<h1>Hello</h1>"}'
# -> 200, a 1-page PDF
```

Two things this drill demonstrated that are easy to mistake for faults:

- **`/health/ready` returning 503 on a new deployment is usually correct.** During the drill
  it was 503 because the S3 credentials supplied were not real — `The Access Key Id you
  provided does not exist in our records`. The check found a genuinely misconfigured
  dependency before any traffic did.
- **A migration failure exits 1 and applies nothing.** Verified: a wrong database password
  produced `28P01: password authentication failed`, exit code 1, and 0 tables created. A
  rollout stops rather than half-migrating.

---

## 4. Health probes — point them at the right endpoints

| Endpoint | Consults dependencies | Point this at |
|---|---|---|
| `/health/live` | **no** | Container HEALTHCHECK, Kubernetes **liveness** |
| `/health/ready` | yes, each with a 3s timeout | Load balancer, Kubernetes **readiness** |
| `/health` | yes, unfiltered | Humans and dashboards |

Getting this wrong is not cosmetic. Before the split, `/health` consulted every dependency
with no timeout of its own: with object storage stopped it **did not answer within 25
seconds**. A liveness probe pointed at that kills a container whose engine is working
perfectly, turning a ten-second bucket blip into a restart loop across every replica at
once. Measured after the split: liveness answers in **0.03s** with storage down, while
readiness correctly reports 503 in 3.1s.

---

## 5. Capacity and memory

Measured on the container at `MaxConcurrentRenders: 2`:

| | Memory |
|---|---|
| At rest | ~850 MB (.NET ~390 MB, Playwright driver ~235 MB, Chromium 218 MB across 3 processes) |
| Under load | ~2.0 GB (Chromium 1.1–1.4 GB across 15–19 processes) |

`mem_limit: 3g` in compose leaves room for the peak plus margin. Running unlimited — the
previous state — means one expensive document can evict everything else on the node.

Chromium sawtooths by design and returns to the resting figure; that is not a leak.
Verified over 10,000 renders: at rest 843 MB after 2,500 and 848 MB after 5,000, i.e. the
first batch pays a bounded warm-up cost and later batches do not.

**Raise `mem_limit` and `MaxConcurrentRenders` together** — they scale as a pair.

Cold start: health at 2.3s, first PDF served at **4.4s**, of which 2.1s is the browser
launch on the first request. An orchestrator's readiness grace period must exceed that or
it will kill containers mid-boot.

---

## 6. Behind a proxy

`UseForwardedHeaders` is enabled for `X-Forwarded-For`, `-Proto` and `-Host`. Without it the
app believes every request arrived over plain HTTP from the proxy's address, which makes
generated absolute URLs wrong, secure-cookie decisions wrong, and collapses every
rate-limit bucket onto one client — the proxy. `KnownNetworks`/`KnownProxies` are cleared
because in a container the proxy is not on a loopback address the defaults recognise;
restrict them if the proxy's address is fixed.

---

## 7. Alerting

`docker/alerts.yml` loads into Prometheus via `rule_files`. Every rule fires on a signal the
chaos testing actually produced:

| Alert | Severity | Why |
|---|---|---|
| `PdfEngineNotReady` | critical | No scrapes for 2 minutes |
| `PdfEngineDependencyUnavailable` | warning | >5% 503s — designed degradation, usually fixed upstream of this service |
| `PdfEngineServerErrors` | critical | 5xx that are **not** 503. Dependency failures are 503 by construction, so anything else is a defect |
| `PdfEngineRenderLatencyHigh` | warning | p95 >5s against a measured baseline of 692ms |
| `PdfEngineMemoryNearLimit` | warning | >2.6 GB sustained against a 3 GB limit |

The 503/500 split is the point. A 500 is a bug report that should wake someone; a 503 is a
retry a client already knows how to do.

---

## 8. Backups

Two things hold state. Neither is backed up by anything in this repository — this is the
procedure, not an implementation.

**PostgreSQL** — tenants, users, API keys, usage and billing records. Losing it loses
customer accounts.

```bash
docker exec pdfengine-postgres pg_dump -U pdfuser -Fc pdfengine > pdfengine-$(date +%F).dump
# restore
docker exec -i pdfengine-postgres pg_restore -U pdfuser -d pdfengine --clean < pdfengine-YYYY-MM-DD.dump
```

Take it on a schedule, store it off the host, and **restore it somewhere at least once** —
an untested backup is a belief, not a backup.

**Verified, not assumed.** The procedure above was exercised on 2026-08-20: dump taken,
restored into a separate database, row counts compared. `pg_restore` reported 0 errors and
all 32 tables and every count matched (Tenants 28, Users 31, ApiKeys 58, PdfJobs 47,226,
UsageRecords 49,571). Do this again on your own infrastructure — a backup verified on a
developer's machine says nothing about a backup taken by your scheduler.

**The drill found something the gates never could.** The dump was **666 MB**, because
`PdfJobs.EncryptedHtmlContent` held **770 MB of submitted HTML across 47,226 jobs** and
nothing ever removed it. `TenantEntitlement.RetentionDays` existed as a field with no code
enforcing it. That is unbounded backup growth and indefinite retention of customer content
that may contain personal data. See §10.

**Object storage** — rendered PDFs for asynchronous jobs. Whether this needs backing up is a
product decision: if job results are retrievable for a limited window and callers can
re-render, replication may be enough. Decide it explicitly rather than by default.

Redis holds the job queue and rate-limit counters. It does not need backing up — losing it
loses queued jobs, which is an availability event, not a data-loss one.

---

## 10. Retention

`TenantEntitlement.RetentionDays` is now enforced by `BillingWorker`, and it is **off by
default**. A service should not start deleting customer data because it was upgraded.

| Setting | Default | Meaning |
|---|---|---|
| `Retention__Enabled` | `false` | Nothing is removed until this is on |
| `Retention__DefaultDays` | `90` | Window for tenants with no `RetentionDays` entitlement |
| `Retention__DeleteJobRows` | `false` | Payload-only by default; `true` removes whole rows |
| `Retention__BatchSize` | `5000` | Per tenant, per pass (every 12 hours) |

**Payload-only is the default on purpose.** The job row survives — status, timings, usage
and audit history are what invoices and support questions are answered from — and only the
submitted content is cleared, which is the part that is both large and sensitive.

Verified against a restored copy of a real database: with a 30-day window, 787 jobs were
cleared, storage fell from **770 MB to 450 MB**, and all 47,226 job rows remained. Every
pass logs what it decided even when it changes nothing, because a retention job that is
silently a no-op looks exactly like one that is working until someone asks why the database
never shrinks.

---

## 9. What is deliberately not implemented

Each is reported by the engine at render time rather than silently degraded: CMYK and PDF/X
(the usual conversion engine is AGPL-licensed, and no validator available here proves PDF/X
conformance), gradient text via `background-clip: text` (Chromium's PDF export does not clip
gradients to glyphs — use SVG text with a gradient fill), and Arabic/Hebrew extraction in
visual order (correct behaviour; `/ActualText` makes the logical text recoverable).
