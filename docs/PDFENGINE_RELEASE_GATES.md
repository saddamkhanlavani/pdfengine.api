# PDFEngine — Release Gates

**Document ID:** PDFENGINE-GATES-001
**Status:** Binding. A release is not "ready" until every applicable gate produces an
auditable PASS from a clean environment.

> A gate PASSES only on reproducible evidence. "It works when I try it" is not a PASS.
> Any gate may be **scoped out** of a release — but only explicitly, in writing, with
> the limitation published in customer-facing docs.

---

## Gate A — HTML/CSS compatibility

Must reliably support **or explicitly diagnose**: HTML5 semantics, block/inline
formatting, Grid, Flexbox, tables, CSS variables, pseudo-elements, generated content,
transforms, borders/backgrounds/gradients/shadows (where PDF supports them), SVG,
Canvas, media queries, `@page`, print CSS, positioning, lists, columns, forms, links.

**Rendering Doctor must detect:** clipping · overflow · hidden content · text ellipsis ·
off-page absolute/fixed elements · fixed-width overflow · missing images · broken fonts ·
unsupported CSS · suspicious print CSS · resource failures.

**PASS =** every unsupported behavior produces a useful diagnostic instead of silent
corruption.

---

## Gate B — Typography (two independent sub-gates)

**B1 — Visual:** glyph correctness, shaping, positioning, weights/styles, fallback chain,
variable fonts where supported.

**B2 — Text layer:** extraction, search, copy/paste, `ToUnicode`, logical (not visual)
ordering, Unicode normalization.

**These are never merged.** A script passing B1 and failing B2 is classified
**visual-only support** and must be documented as such.

Per script, required: visual screenshot comparison · extraction diff vs source ·
search test · copy/paste test · font embedding check · mixed-script test.

**Mixed-direction fixtures are mandatory** (uniform-RTL blocks hide ordering bugs):
Arabic+English · Arabic+numbers · Arabic+SKUs · Hebrew+English · RTL table cells ·
RTL/LTR inline phrases · punctuation · dates · currency · identifiers.

**Extraction oracle:** poppler `pdftotext` plus one independent extractor.
**Do not use PyMuPDF as the oracle** — it misreported Devanagari conjuncts during this
programme and would produce false failures.

---

## Gate C — Pagination intelligence

Natural flow · explicit breaks · `break-before/after/inside` · headings near page
bottoms · widows/orphans · keep-with-next · keep-together · tables crossing pages ·
figures/captions · lists · multi-column · nested containers · headers/footers ·
first/left/right pages · multiple page sizes · mixed orientation · named pages ·
cover pages · trailing pages · blank-page detection.

**PASS =** no unexplained blank pages, no unexplained clipping/overflow, no unresolved
pagination defect across the corpus.

---

## Gate D — Page utilization

Must detect: large bottom whitespace · stranded headings · oversized unbreakable
elements · forced-break waste · figure/caption separation · table fragmentation ·
page-boundary anomalies.

Must **attribute cause**: user-requested break · keep-together · unbreakable element ·
browser decision · optimizer decision · intentional design/cover · unknown.

**Hard rule:** no optimizer may move content solely to raise utilization if that
violates semantic or explicit author intent.

**PASS =** `avoidable whitespace = 0`, `orphan headings = 0`, `unexpected blank pages = 0`,
`content overflow = 0` across the corpus.

---

## Gate E — Tables

Repeating headers · repeating footers · safe row splitting · `break-inside: avoid` ·
**rowspan and colspan across a real page boundary** · nested tables · grouped rows ·
subtotals · grand totals · wide-table overflow policy · extremely tall cells · large row
counts · deterministic breaks.

Explicit table policies required: `fit` · `shrink` · `landscape` · `split` · `overflow` ·
`fail-with-diagnostic`.

---

## Gate F — Figures & charts

SVG · Canvas · Chart.js · D3 · CSS charts · images · QR · barcodes · **figure–caption
atomic grouping** · predictable scaling · high-resolution assets · image optimization ·
transparency · gradients · clipping/masks.

**Hard rule:** a caption must never be stranded from its figure across a page break.

---

## Gate G — Document navigation

Bookmarks/outline · internal links · external links · table of contents ·
**page-number cross-references derived from actual final PDF page numbers, never a
simulated counter** · named anchors · destinations · metadata · document language ·
structure tree.

---

## Gate H — PDF output features

Password protection · user/owner passwords · **AES-256 where supported** · granular
permissions (**AES-256 achieved — RB-1 closed**) · watermark · metadata · merge · split · rotate · flatten · attachments ·
PDF/A profiles · **PDF/UA only when independently validated** · linearization ·
digital signatures/PAdES (separate controlled subsystem) · Factur-X/ZUGFeRD if
commercially prioritized.

---

## Gate I — Security

**Input:** HTML/CSS sanitization · JS policy · dangerous protocol blocking · malformed
input · entity/resource limits · DOM depth and node-count limits · script timeout ·
page-count and document-size limits · compressed/decompressed size limits.

**Network (SSRF):** loopback · private IPv4 · private IPv6 · link-local · cloud metadata ·
DNS rebinding · redirects (re-validated **every hop**) · IPv4-mapped IPv6 · decimal/octal
IP forms · hostname resolution changes. Plus allowlists, blocked ports, timeouts,
redirect limits.

**Sandbox:** isolated browser process and context · restricted permissions · no
unnecessary filesystem/device access · no credential leakage · CPU/memory/process limits.
**Renderer workers should hold zero production secrets.**

**API:** authN · authZ · tenant isolation · API-key hashing/rotation/revocation · rate
limits · quotas · request size limits · idempotency · replay protection · CORS · secure
headers · audit logs.

**Storage:** private buckets · signed URLs with short expiry · tenant-bound paths ·
download authorization · deletion lifecycle · encryption at rest.

**Automated suites required:** SSRF fuzzing · XSS fuzzing · HTML/CSS parser fuzzing ·
ZIP/decompression bombs · path traversal · credential leakage · tenant escape ·
rate-limit bypass · authorization bypass · webhook abuse · signed-URL abuse · oversized
requests.

---

## Gate J — Determinism

Pinned Chromium · pinned render profile · engine versioning · font version locking ·
asset cache versioning · timezone/locale control · deterministic date/time injection ·
seeded randomness · network isolation · stable JS readiness · repeat-render hashing ·
PDF structural fingerprint · perceptual visual fingerprint.

**PASS =** same input + same engine version + same profile + same assets ⇒ materially
identical output. Any unavoidable variance documented and bounded.

---

## Gate K — Performance

Benchmark: 1 / 5 / 20 / 100 / 500 / 1,000 pages · 10,000-row table · concurrent renders ·
cold start · warm start · font-heavy · chart-heavy · image-heavy.

Measure: queue latency · browser startup · context startup · navigation · asset time ·
font time · layout · pagination · PDF export · post-processing · storage · total latency ·
CPU · memory · file size.

**PASS =** thresholds met **and** a sustained 10,000-render run proves memory stays bounded.

---

## Gate L — Reliability & chaos

Inject: browser crash · Chromium hang · Redis outage · DB outage · storage outage · slow
assets · broken fonts · DNS failure · full disk · worker termination · network partition ·
webhook destination failure.

**PASS =** fails cleanly · does not leak jobs · does not duplicate billable renders ·
retries only when safe · preserves diagnostics · recovers automatically where possible.

---

## Gate M — Platform

Login/auth flows · API flows · billing flows · webhook flows · storage flows ·
**dashboard contains no mocks** · status/health is real · signed downloads work ·
audit logs work.

---

## Release certification checklist

**Rendering:** 100+ template corpus passes · Gold documents pass · adversarial documents
pass · no unexplained blank pages/clipping/overflow · utilization audit passes · table,
typography and asset certifications pass.

**PDF correctness:** page count · metadata · bookmarks · links · **page references** ·
font embedding · **text extraction for every claimed script** · encryption ·
permissions · PDF/A validated where claimed · PDF/UA validated only where genuinely
supported.

**Security:** no unresolved critical/high · SSRF suite · tenant isolation · auth ·
rate limits · fuzzing baseline · dependency scan.

**Performance:** benchmarks · concurrency · sustained run · bounded memory · browser
recycling · queue recovery.

**Platform:** auth · API · billing · webhooks · storage · no mocks · real health ·
signed downloads · audit logs.

---

## Quality scoring & P0 definition

```
Rendering correctness  30%     Accessibility   10%
Pagination correctness 20%     PDF integrity    5%
Typography             10%     Performance      5%
Tables                 10%     Security         5%
Graphics                5%
```

**Independently of score: `P0 failures = 0` and `P1 failures = 0`.**

**P0 =** blank page · missing content · wrong page count · corrupted PDF · wrong table
data · wrong text · wrong link destination · cross-tenant data exposure.

---

## Gate status board — as of 2026-08-16

**No gate has formally PASSED.** A gate PASSES only when an automated runner produces a
reproducible verdict from a clean environment. Manual verification during development is
evidence for the *registry*, not a gate pass.

| Gate | Area | Automated runner | Status |
|---|---|---|---|
| **A** | **HTML/CSS contract + Rendering Doctor** | ✅ `tests/compat_gate.py` | **ARMED** — 11/11 with Gate B1. Grid/Flex/vars, pseudo-elements, transforms/shadows/gradients, `@page`+print CSS. **Found and fixed two real defects on its first runs:** (1) the CSS sanitizer stripped the `size` descriptor from `@page` while keeping `margin`, so EVERY document rendered A4 regardless of the size it requested; (2) the Doctor did not detect overflow or off-page content at all — both now emit warnings |
| **B1** | **Typography — visual** | ✅ `tests/compat_gate.py` | **ARMED** — webfont embedding, tofu detection, fallback chain, distinct weights. **Found and fixed a real defect:** every `@font-face` was declared `font-weight: 100 900` from a single Regular file, so weights 100/400/900 produced byte-identical ink — bold was never bold. Two bundled files were also mislabeled (`Outfit-Regular.ttf` was Outfit **Thin**) |
| **B2** | **Typography — text layer** | ✅ `tests/extraction_gate.py` | **ARMED** — evidence [`text-extraction-gate.log`](../tests/evidence/text-extraction-gate.log), exit 0. Not "PASS": 2 fixtures are PARTIAL by design (upstream Arabic/bidi) |
| **C** | **Pagination intelligence** | ✅ `tests/pagination_gate.py` | **ARMED** — 7/7. Every fixture is a regression test for a bug that actually shipped: grid-nested break, native-break desync, ToC page refs, keep-together, orphan heading. **Found a real ligature bug on its first run** |
| **D** | **Page utilization** | ✅ `tests/pagination_gate.py` | **ARMED** — asserts whitespace is reported *with an attributed cause*, and that no unexplained blank pages occur in a mixed real-world document |
| **E** | **Tables** | ✅ `tests/table_gate.py` | **ARMED** — 6/6 incl. **rowspan across a real page boundary** (the previously untested weak spot), colspan, thead repetition over 10 pages, running-total reconciliation, taller-than-page row, 25-col wide table. Asserts data integrity, not aesthetics |
| **F** | **Figures & charts** | ✅ `tests/figures_gate.py` | **ARMED** — 7/7. Asserts on rasterised PIXELS, not on absence of error, because the canonical failure (DEFECT-005) is a blank canvas inside a structurally valid PDF. Covers SVG/gradients, canvas charts, figure–caption atomicity, transparency, image optimization, CSS charts, predictable scaling |
| **G** | **Document navigation** | ✅ `tests/navigation_gate.py` | **ARMED** — 7/7. Outline, internal/external link annotations, metadata, `/Lang`, structure tree, and cross-references checked against the REAL rendered page. **Found and fixed a real defect on its first run:** a cross-reference to a non-existent id produced zero page-ref requests, so the whole resolution pass was skipped and the entry shipped as a silent blank gap |
| **H** | **PDF/A + PDF/UA conformance** | ✅ `tests/conformance_gate.py` | **ARMED** — 6/6 conformant, automated in CI. PDF/A-2b 144/0, PDF/A-3b 146/0, PDF/UA-1 106/0. Validator preflight distinguishes a tooling failure from a real regression. *Gate H not fully PASSED:* forms/attachments/split/rotate/flatten/signing still MISSING |
| **I** | **Security** | ✅ `tests/security_gate.py` | **ARMED (PARTIAL by design)** — 16/16. Sanitization, inline-handler stripping under `allowScripts`, dangerous URIs, SVG script, 8 SSRF address forms, sub-resource SSRF, resource limits. **Found and fixed a REMOTE DoS:** ~6,000 nested elements overflowed AngleSharp's parser stack and killed the ENTIRE API process for all tenants (uncatchable in .NET; now refused at validation). Also fixed silent SSRF sub-resource drops. **Not covered:** sandbox escape, worker credential leakage, signed-URL/webhook abuse, storage lifecycle, fuzzing suites |
| **J** | **Determinism** | ✅ `tests/determinism_gate.py` | **ARMED** — 5/5 stable over 3 runs each. Structural+visual+size fingerprints with volatile /ID & timestamps excluded. **Engine-version pinning now shipped:** every response carries `X-PdfEngine-Engine-Version` (e.g. `2026.08.1+chromium145.0.7632.6`) and `pinEngineVersion` REFUSES the render on mismatch rather than silently using a different Chromium. `fixedDateUtc`, `randomSeed`, `timezone` and `locale` make clock/locale-dependent documents reproducible — the `clock-and-random` fixture cannot pass without them. Drift detection proven live: the `@page size` fix changed all 5 pinned fingerprints and the gate FAILED until the profile was bumped to `2026.08.1`. *Not PASS:* cross-machine reproducibility and a visual-diff API still MISSING |
| **K** | **Performance** | ✅ `tests/performance_gate.py` | **ARMED (PARTIAL by design)** — 8/8. Page-count scaling 1/5/20/100, per-page cost ratio, 10,000-row table (219 pages / 3.2s), 8-way concurrency, sustained-run leak proxy. Opt-in via `RUN_PERF_GATE=1`; runs nightly, not per-push, because thresholds are machine-relative. **Not covered:** the full 10,000-render soak, cold-start from a cold container, 500/1000-page documents |
| **L** | **Reliability / chaos** | ✅ `tests/reliability_gate.py` | **ARMED (PARTIAL by design)** — 7/7. Unreachable assets, broken webfonts, DNS failure categorisation, malformed HTML, slow-asset non-hang, **recovery after the browser process is killed**, and failure non-contagion. **Not injected:** Redis/DB/storage outage, full disk, network partition, webhook destination failure |
| M | Platform | ✅ `tests/platform_gate.py` | **ARMED** — 17/17 incl. two-tenant isolation. Found+fixed a live P0 cross-tenant leak. Not PASS: auth flows, billing, webhooks, key rotation uncovered |

**Scoreboard: 13 gates defined · 13 armed · 0 not started · 0 fully passed.**

Every gate now has a runner and a committed baseline. **Armed is not passed.** Gates I, K
and L are PARTIAL *by design* — each prints exactly what it does not cover, and those gaps
are listed in their rows above. The six gates added on 2026-08-18 found **six real
defects** between them within minutes of first execution, including a remote
denial-of-service that killed the whole API process.

**CI:** `.github/workflows/release-gates.yml` runs `tests/run-gates.sh` — the *same*
entry point developers run locally, so CI and local behaviour cannot drift. It
provisions Postgres, Redis, poppler, veraPDF and Chromium, starts the API, runs every
armed gate and uploads `tests/evidence/` as a build artifact.
Latest local run: **ALL AVAILABLE GATES PASSED** (`tests/evidence/run-gates.log`).

A `SKIP` is never a pass: the runner exits 2 when a gate is skipped and 3 when the
toolchain itself is wrong (e.g. a `dotnet` on PATH without the .NET 8 runtime, which
previously surfaced as a fake "unit tests FAIL").

`ARMED` = runner exists, baseline committed, regressions detected.
`PASS` = runner exists **and** all fixtures meet the target verdict **and** it runs in CI.

---

## Current open blockers

See §9 of [`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md) —
**Open:** none of RB-1..RB-6. RB-2 closed 2026-08-18 via `/ActualText` on RTL runs.

**Defects found by the gates added 2026-08-18** (all fixed, all now regression-locked):
1. **Remote DoS** — deeply nested HTML overflowed the parser stack and terminated the API process for every tenant (Gate I)
2. **`@page { size }` never worked** — the CSS sanitizer stripped the descriptor; all documents rendered A4 (Gate A)
3. **Bold was never bold** — one Regular font file was declared as the whole 100–900 range (Gate B1)
4. **Two mislabeled font files** — `Outfit-Regular.ttf` / `Montserrat-Regular.ttf` were the Thin weights (Gate B1)
5. **Silent blank cross-references** — a dangling `data-pdfengine-pageref` skipped resolution entirely (Gate G)
6. **Silent SSRF sub-resource drops** — blocked assets surfaced only as a generic `net::ERR_FAILED` (Gate I)
**Closed with evidence:** ~~RB-1~~ (AES-256) · ~~RB-2~~ (RTL `/ActualText`, extraction gate FAIL=0) · ~~RB-3~~ (extraction gate) ·
~~RB-4~~ (PDF/UA-1 106/0) · ~~RB-5~~ (platform gate 17/17 + P0 leak fixed) ·
~~RB-6~~ (ICC licence).
