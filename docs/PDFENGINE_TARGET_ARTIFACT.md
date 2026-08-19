# PDFEngine — Target Artifact & Master Roadmap

**Document ID:** PDFENGINE-TARGET-001
**Version:** 1.1 (reconciled against branch `main`, 2026-08-16)
**Status:** Living engineering contract. **Not a sales document.**

> **Governing sentence.** PDFEngine is finished only when the engine can demonstrate,
> with reproducible evidence, that it renders the supported real-world document corpus
> correctly, intelligently handles pagination and assets, preserves required text
> semantics, fails safely outside its contract, remains deterministic and performant
> under load, and operates as a secure production SaaS platform.

---

## 0. Executive decision

PDFEngine is **not** finished because flagship PDFs look good.

The central rule: **a capability is not "done" because code exists.** It is done only
when implementation + automated tests + adversarial tests + PDF-internal inspection +
performance evidence + documentation all agree.

**Priority order is deliberate and must not be reordered:**

```
Verification foundation
  → Core rendering correctness
    → Pagination intelligence
      → Tables
        → Typography / text-layer fidelity
          → Assets & charts
            → Paged media (Prince parity)
              → PDF object model
                → Accessibility
                  → Determinism
                    → Performance
                      → Security
                        → API / platform
                          → Auth hardening
                            → Billing
                              → Deployment
                                → Commercial launch
```

Do **not** move into payments, domains, or deployment while the rendering core has
unresolved correctness gaps. That ordering exists to prevent building a beautifully
secured SaaS around a renderer with edge cases customers will find first.

---

## 1. Product promise (the exact wording permitted)

**Permitted:**

> Give PDFEngine normal web technologies and it produces a stable, professional PDF
> while automatically detecting and preventing common print-layout failures.

**Release target:**

> Zero known certification failures across a deliberately broad, adversarial and
> continuously expanding corpus, with deterministic behavior and explicit diagnostics
> for cases outside the supported contract.

**Forbidden** (see §9 Anti-overclaim policy): "supports all CSS", "zero errors for any
HTML", "perfect PDF generation", "100% PDF/UA", "all languages supported", "only
engine that supports X".

---

## 2. Status vocabulary

**VERIFIED** · **IMPLEMENTED** · **PARTIAL** · **REPORTED** · **NOT VERIFIED** ·
**MISSING** · **BLOCKED BY UPSTREAM** · **DEFERRED** · **RELEASE BLOCKER**

Definitions and the full per-capability breakdown live in
[`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md).
**That registry is the only permitted source for a sales artifact.**

---

## 3. Architecture — current and target

**Current (correct direction, keep it):**

```
Request → Sanitize → Asset optimize → DOM analyze → Layout analyze
        → Typography → Pagination plan → Chromium render
        → Post-layout verify → PDF compile → PDF verify → Diagnostics
```

**Strategic constraint:** do **not** replace Chromium/Skia/HarfBuzz. The intelligence
belongs *around* Chromium, not instead of it:

```
inspect → predict → constrain → render → measure → detect defect
        → safe correction → re-render when beneficial → verify → report
```

**Target additions:** Semantic Document Model → Constraint Solver → Page/Table/Figure/
Footnote Optimizers → PDF Object Model layer → Verification & Explainability layer.

---

## 4. The three levels of capability

| Level | Question | Our state |
|---|---|---|
| **1 — Browser compatibility** | Can Chromium render this HTML? | Largely achieved |
| **2 — Document intelligence** | Can the engine detect that Chromium's natural choice makes a bad document, and correct it safely? | **Partially achieved — our biggest opportunity** |
| **3 — Professional paged media** | Can we do what Prince/DocRaptor do? | Mostly missing |

Level 2 is the differentiator. Level 3 is the parity program.

---

## 5. Release gates (summary)

Full detail in [`PDFENGINE_RELEASE_GATES.md`](./PDFENGINE_RELEASE_GATES.md).

- **Gate A** — HTML/CSS compatibility contract, or explicit diagnostic
- **Gate B** — Typography: **visual and text-layer certified separately, per script**
- **Gate C** — Pagination intelligence across all break semantics
- **Gate D** — Page utilization with *attributed cause*, never optimizing against author intent
- **Gate E** — Tables incl. rowspan/colspan across page boundaries
- **Gate F** — Figures/charts with figure–caption atomicity
- **Gate G** — Navigation: bookmarks, links, cross-references from **real final page numbers**
- **Gate H** — PDF output features, each independently validated

---

## 6. Master roadmap — phases

Statuses below are **reconciled against the current branch**, not inherited from
earlier reports.

### Phase 0 — Verification foundation `SUBSTANTIALLY DONE` (2026-08-18)
71 unit tests plus **13 automated release gates, every one with a committed baseline and
regression detection**, run from a single entry point (`tests/run-gates.sh`) that CI uses
verbatim. Latest full run: unit 71 · B2 14 PASS/4 SPACING/1 PARTIAL · M 17/17 · C+D 7/7 ·
E 6/6 · A+B1 11/11 · F 7/7 · G 7/7 · I 16/16 · L 7/7 · J 5/5 · H 6/6 · K 8/8 (opt-in).
Evidence: [`run-gates.log`](../tests/evidence/run-gates.log).

**Still missing:** load testing at production scale, fuzzing suites, the full
10,000-render soak, and the 100+ fixture corpus. Gates I, K and L are PARTIAL by design
and each prints its own uncovered surface.
*Exit rule unchanged:* no workstream may claim completion without automated evidence.

### Phase 1 — Core HTML/CSS contract `IN PROGRESS`
Grid/Flex/SVG/JS verified and now gated (`tests/compat_gate.py`, 11/11). Rendering Doctor
now diagnoses **overflow** and **off-page content** — it previously detected neither.
Building this gate also exposed that `@page { size: ... }` had never worked (the CSS
sanitizer stripped the descriptor) and that **bold was never bold** (one Regular font file
declared as the entire 100–900 weight range); both are fixed and regression-locked.
*Remaining:* ellipsis/clipping diagnostics and a full compatibility matrix.

### Phase 2 — Typography & i18n `PARTIAL` · **BLOCKER**
Latin + web fonts verified. **Arabic text layer BLOCKED BY UPSTREAM.** Devanagari
largely correct. CJK visual only. 9+ scripts have no fixtures. Font fallback MISSING.
*Release rule:* a script that renders visually but has a broken text layer is
classified **visual-only support** until resolved or explicitly scoped.

### Phase 3 — Pagination intelligence `IN PROGRESS` · **BLOCKER**
Native-break resync, grid-aware suppression, orphan avoidance, keep-together and
attributed whitespace diagnostics are verified. The weighted **constraint solver /
candidate-scoring optimizer is MISSING** — this is the Level-2 differentiator.

### Phase 4 — Advanced tables `PARTIAL` · **BLOCKER**
Repeating headers verified over 18 pages. Rowspan protection implemented but never
tested across a real page boundary. Wide-table policy, row fragmentation, subtotals,
landscape fallback all MISSING.

### Phase 5 — Figures & assets `PARTIAL` · **BLOCKER**
Real WebP re-encode verified. Figure–caption atomic grouping, format-aware policy
(photo→JPEG/WebP, line-art→PNG), SVG complexity limits, QR/barcode fixtures MISSING.

### Phase 6 — Paged media `MISSING` · *not initially blocking*
Named pages, `@page :first/:left/:right`, `string-set`, running elements, leaders,
footnotes, page floats, multi-column balancing, mixed orientation, bleed, crop marks.
**This is the Prince/DocRaptor parity program.** Standard `target-counter()` syntax
matters more than the feature itself — it is the migration path for their customers.

### Phase 7 — PDF object model `PARTIAL` · **BLOCKER for advertised features**
Metadata, bookmarks, links, merge, watermark, PDF/A verified. Forms, attachments,
split/rotate/flatten, linearization, signing MISSING. **Encryption is RC4-128, not
AES-256 — RB-1.**

### Phase 8 — Accessibility `PARTIAL (UA-1 structurally achieved)` · **BLOCKER for broad accessibility claims**
Real structure tree verified present. **PAC never run; Arabic text layer broken.**
"PDF/UA compliant" is a certification claim, not a code checkbox — do not ship it.

### Phase 9 — Determinism `MISSING` · **BLOCKER**
No engine version pinning, no reproducibility fingerprint, no visual-diff API.
**Highest-leverage differentiator — nobody in this market sells it.**

### Phase 10 — Performance `PARTIAL` · **BLOCKER**
23 pages in ~1.0s cold / ~0.6s warm, measured. Prior 1,000-page and 10,000-row runs
were one-off demos. Sustained 10,000-render memory-bound test MISSING.

### Phase 11 — Security hardening (Track B) `PARTIAL` · **BLOCKER**

> **Security is NOT a phase.** It is split into two tracks, because a single "Phase 11"
> wrongly implies the live security surface can wait its turn. It cannot — it is already
> exposed to users today.

**Track A — always-on, maintained continuously from now (not deferred):**
HTML/CSS sanitization · SSRF + DNS-rebinding + redirect re-validation · resource byte
limits · rate limiting · tenant isolation · secret handling · authentication boundaries ·
browser isolation · dangerous protocol blocking.
*Current state:* implemented and partially verified (see registry §6). Any regression
here is a **P0**, at any point in the roadmap, regardless of which phase is active.

**Track B — progressive hardening, sequenced here:**
Fuzzing (HTML/CSS/SSRF) · ZIP & decompression bombs · DOM depth/node limits ·
sandbox-escape testing · container hardening · egress policy · SBOM + dependency
scanning · penetration testing · secret rotation · audit logging · abuse detection ·
tenant-bound authorization tests · API-key lifecycle tests · 2FA tests ·
rate-limit bypass tests · resource-exhaustion tests.

**The roadmap must never be read as "security comes after rendering."** The correct
statement is: *rendering is the primary development priority, while security is
continuously maintained (Track A) and progressively hardened (Track B) throughout.*

### Phase 12 — Identity & auth `REPORTED` · **BLOCKER**
11 controllers exist. **Zero integration tests.** Must be revalidated, not inherited.

### Phase 13 — API productization `PARTIAL` · **BLOCKER**
Sync + async + batch + webhooks + diagnostics exist. Idempotency keys, cancellation,
API versioning, signed downloads MISSING.

### Phase 14 — Developer experience `PARTIAL` · *not initially blocking*
Per-stage timings + attributed warnings already returned. Should become the product:
render timeline, waterfall, page screenshots, visual diff, debug bundle.

### Phase 15 — Observability `PARTIAL` · **BLOCKER**
Per-render diagnostics real. Operational P50/P95/P99, queue depth, crash counters
unverified. **No dashboard metric may be fabricated or hardcoded.**

### Phase 16 — Chaos & reliability `MISSING` · **BLOCKER**
Browser crash, Chromium hang, Redis/DB/storage outage, worker kill, webhook failure —
each needs a defined expected behavior and a failure-injection test.

### Phase 17 — Billing `PARTIAL` · **BLOCKER before paid launch**
Usage ledger must be independent of Stripe. Idempotent payment processing required.

### Phase 18 — Deployment `NOT VERIFIED` · **BLOCKER**
Domain, TLS, secrets, CI/CD, migrations, backups + **restore rehearsal**, rollback, DR.
Renderer workers must be isolated from the API process and hold **zero production secrets**.

### Phase 19 — Release certification `NOT STARTED` · **BLOCKER**
A clean-environment run producing an auditable PASS/FAIL report.

---

## 7. Differentiation strategy

Do not try to win by claiming every feature. Ranked by defensibility:

1. **Determinism + visual regression as a product.** Engine version pinning, long-lived
   old versions, snapshot-diff endpoint, CI failure on regression. Nobody sells the fix
   for "Chromium updated and last month's invoice reflowed." Switching-cost moat.
2. **Intelligent pagination with explainability.** Not "the heading moved" but *"only two
   lines of the following section would have fit; three placements evaluated; lowest-cost
   legal layout chosen; verified; here is why."*
3. **Diagnostics as a feature.** "Why did my PDF look wrong" is the #1 support ticket in
   this category. Every competitor is a black box. We already return per-stage timings
   and attributed whitespace causes — productize it.
4. **Accessibility done properly** — *after* the text layer is fixed, not before.
5. **Prince/DocRaptor CSS compatibility mode.** Their customers are locked in by
   stylesheets, not APIs. Support standard `target-counter`/`string-set`/footnotes and
   they migrate by changing an endpoint.
6. **Signing + e-invoicing (PAdES + Factur-X/ZUGFeRD).** No HTML→PDF API does both.

**Explicitly NOT differentiators** (Gotenberg ships them free, self-hosted): merge,
PDF/A, tagged output, watermark, encryption, metadata, split/rotate/flatten.

---

## 8. Required intelligence subsystems

**Page Optimizer** — `measure → generate candidates → score → apply safe candidate →
re-render → compare → keep/revert`

**Constraint priority (non-negotiable):**

```
HARD   : explicit break-before/after, page size, orientation,
         absolute positioning, table semantics, link destinations,
         accessibility structure, writing direction, aspect ratio
STRONG : keep-with-next, keep-together, figure–caption atomicity,
         table row constraints
SOFT   : whitespace, balancing, aesthetic movement
```

An optimizer must **never** move content merely to improve utilization if that violates
explicit author intent. Also required: **Table Optimizer**, **Figure Optimizer**,
**Footnote Optimizer**, **Constraint Solver**, **Learning Diagnostics** (deterministic
telemetry first; learned heuristics only when explainable and bounded).

**Render modes to expose:** `strict` (author CSS authoritative) · `intelligent`
(optimize within hard constraints) · `max-fidelity` (safe corrections only).

---

## 9. Anti-overclaim policy — mandatory

Every documented capability must carry:

```
Capability → Scope → Test → Evidence → Known limitations
```

**Never claim** accessibility from tags alone. **Never claim** language support from
visual rendering alone. **Never** convert subset evidence into a universal claim.

---

## 10. Engineering workflow — mandatory per feature

```
1 Inspect existing behavior
2 Write a FAILING test that reproduces the defect
3 Implement the smallest correct architectural change
4 Unit test        5 Integration test
6 Visual + PDF-internal verification
7 Adversarial verification
8 Full regression  9 Performance impact
10 Generate evidence artifact
11 ONLY THEN update status in the capability registry
```

### Non-negotiable quality rules

1. Never skip a failing test. 2. Never disable an assertion for green CI. 3. Never mock
away a real implementation to pass. 4. Never update a visual baseline unreviewed.
5. Never call a feature verified without evidence. 6. Never hide a known limitation in
sales material. 7. Never silently fall back when the fallback changes output — warn or
fail. 8. Never let the optimizer violate author intent. 9. Never allow cross-tenant
artifact access. 10. Never let customer HTML become a privileged execution environment.
11. Never ship a security feature without adversarial tests. 12. Never fabricate a
production metric. 13. Never make billing depend solely on client state. 14. Never claim
accessibility from tags alone. 15. Never claim language support from visuals alone.

---

## 11. Roadmap control board

| Phase | Area | Status | Evidence required | Blocker |
|---|---|---|---|---|
| 0 | Verification foundation | IN PROGRESS | permanent CI corpus | **YES** |
| 1 | HTML/CSS core | IN PROGRESS | compatibility matrix | **YES** |
| 2 | Typography / i18n | PARTIAL | visual **+ extraction** per script | **YES** |
| 3 | Pagination intelligence | IN PROGRESS | optimizer regression corpus | **YES** |
| 4 | Tables | PARTIAL | rowspan/colspan across page break | **YES** |
| 5 | Figures / assets | PARTIAL | chart/image/QR/barcode suite | **YES** |
| 6 | Paged media | MISSING | standards compatibility suite | no → yes for advanced profile |
| 7 | PDF object model | PARTIAL | structural inspection per feature | **YES** for advertised |
| 8 | Accessibility | NOT VERIFIED | PAC + veraPDF + extraction + screen reader | **YES** for a11y claims |
| 9 | Determinism | MISSING | repeat-render fingerprint suite | **YES** |
| 10 | Performance | PARTIAL | concurrency + 10k sustained | **YES** |
| 11 | Security — Track A (live) | MAINTAINED | continuous; any regression = P0 | **ALWAYS** |
| 11 | Security — Track B (hardening) | PARTIAL | fuzzing + SBOM + pen test + threat model | **YES** |
| 12 | Identity / auth | REPORTED | full auth integration suite | **YES** |
| 13 | API | PARTIAL | contract / integration suite | **YES** |
| 14 | Developer experience | PARTIAL | end-to-end diagnostics workflow | no |
| 15 | Observability | PARTIAL | real metrics, no mocks | **YES** |
| 16 | Chaos / reliability | MISSING | failure-injection suite | **YES** |
| 17 | Billing | PARTIAL | payment reconciliation suite | **YES** before paid launch |
| 18 | Deployment | NOT VERIFIED | production rehearsal | **YES** |
| 19 | Release certification | NOT STARTED | signed release report | **YES** |

Statuses are intentionally conservative. Earlier reports saying "PASS" for a subset do
not convert into universal claims.

---

## 12. Sales artifact policy

```
Target Artifact → implemented capability → test → evidence
   → certification → VERIFIED CAPABILITY REGISTRY → Sales Artifact
```

**If a capability is not in the registry as VERIFIED, it may not appear in marketing.**
The sales document is never the source of truth.

---

## 13. Immediate next actions

**P1 — Freeze the baseline.** ✅ **DONE 2026-08-16.** All 18 pre-existing `docs/` files
reconciled and banner-marked: 5 SUPERSEDED (status claims), 4 SUPERSEDED (plans/checklists),
1 SUPERSEDED (unverified performance numbers), 8 REFERENCE/HISTORICAL. `tests/corpus/`
and `tests/evidence/` created.

*Why this mattered:* an external review citing `docs/` reported "only 24 automated tests
exist" while the branch actually had **57 passing**. Stale documents were already being
read as current truth — exactly the failure this registry exists to prevent.

**P2 — Close the correctness blockers, in this order:**

1. ✅ **DONE — Text-extraction gate** (`tests/extraction_gate.py`). 15 scripts, poppler
   oracle, committed baseline, regression detection self-tested. Measured:
   `PASS=9, SPACING=4, PARTIAL=2, FAIL=0`. Overturned the prevailing diagnosis — the
   dominant defect is word-boundary **spacing** at script transitions, not broken
   `ToUnicode`.
2. ✅ **DONE — Arabic claim scoped** in the registry. Still open: surface the same
   verdict at runtime in the API diagnostics
   (`{"language":"ar","visualRendering":"pass","textExtraction":"engine-limited"}`).
3. ✅ **DONE — RB-6 ICC licence closed.** Profile now *generated* by Little CMS (MIT)
   from IEC 61966-2-1 constants; nothing vendor-owned redistributed. Re-validated:
   PDF/A-2b 144/0, PDF/A-3b 146/0 across 4 documents.
4. **RB-1 encryption** — decide on PDFsharp 6.2+ / alternative for AES-256. **Next.**
   Blast radius: metadata, outline, watermark, encryption, merge — all currently
   VERIFIED and all must be re-verified after any library change.
5. **Rowspan across a real page boundary** — the known weak spot (Gate E).
6. **veraPDF + PAC in CI** — veraPDF evidence now exists but runs manually; PAC never
   run. Do not claim PDF/UA (**RB-4**) until both pass.
7. **RB-5** — build the integration test host for auth/tenancy/billing.

**P3 — Build the constraint-solver Page Optimizer** (the Level-2 differentiator).

**P4 — Only then** move outward to security hardening → API/platform → auth → billing →
deployment.

---

## 14. What this roadmap deliberately does NOT do

- It does not promise mathematically zero visual errors for arbitrary HTML — that is
  technically indefensible with arbitrary input, external assets and browser behavior.
- It does not treat a passing flagship document as proof of engine completeness.
- It does not let breadth demonstrations substitute for systematic intelligence.
- It does not permit inherited "REPORTED" status to become a launch claim.
