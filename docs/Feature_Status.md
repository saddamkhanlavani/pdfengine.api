> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Capability and status claims in this file are **historical**. They are NOT
> authoritative and must not be used for engineering decisions, release gating,
> or any customer-facing claim.
>
> **Authoritative source:** [`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md)
>
> Superseded 2026-08-16. Retained only as a record of what was believed at the time.

# PDFEngine Feature Status Matrix

**This document previously reported precise pass counts (e.g. "84/90 CSS Grid tests
passed") and benchmark figures (e.g. "99.98% success over 10,000 runs", "P95 345ms")
that no test suite in this repository produces.** `scripts/run_certification_suite.py`
exercises five inline HTML snippets and a 100-request load test of a single-line
document — it does not, and cannot, produce a CSS Grid/Flexbox pass-rate, an OCR or
barcode decode rate, or a 10,000-run success percentage. Those numbers have been
removed rather than corrected, because there is nothing to correct them *to* — no
harness in this repo currently measures them. Rebuilding this matrix honestly means
building the missing test coverage first, not restating better-sounding fake numbers.

What follows is status only, not fabricated metrics. See `Implementation_Status.md` for
the subsystem-level detail and file:line evidence behind each entry.

---

## ➔ Subsystem Status Summary

| Subsystem | Status | Real automated coverage today |
| :--- | :--- | :--- |
| HTML Sanitizer | Implemented | `HtmlSanitizerStageTests.cs` (6 tests) |
| DOM Analyzer | Partial | `DomAnalyzerTests.cs` (4 tests) |
| Layout Analyzer | Partial (advisory-only) | None |
| Pagination Planner | Partial | `PaginationPlannerTests.cs` (6 tests) |
| Typography Engine | Partial | None |
| Asset Optimizer | Partial (warn-only) | None |
| Print Optimizer / page geometry | Implemented | Manual render verification (see below); no automated test yet |
| Table Subsystem | Partial | None |
| Resource Loader | Implemented (narrow) | None |
| Security Engine (SSRF, rate limiting, secrets) | Implemented | None automated yet — verified manually this pass; automated coverage is backlog |
| Browser Orchestrator | Implemented | None |
| Verification Engine (visual diff) | Cosmetic | None |
| PDF Metadata | Implemented | `PdfMetadataTests.cs` (2 tests) |

Total automated test count in this repo as of this pass: **24** (up from 3), covering
the areas listed above. Every other cell in this table has zero automated coverage —
that is not a euphemism for "well-tested but unlisted," it means no test exists.

## ➔ Performance & Quality Benchmarks

**Removed.** The previous table's numbers (render success rate, P95/P99 duration, OCR
read success, barcode/QR decode rate, peak worker memory) were not backed by any
benchmark harness in this repository — no OCR or barcode-decode code exists in the
source at all, and no load test beyond the 100-request smoke test in
`run_certification_suite.py` exists. Publishing real numbers here requires building
real load-test and accuracy-measurement tooling first. Until that exists, this section
stays empty rather than restating invented figures.
