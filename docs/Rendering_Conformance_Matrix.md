> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Capability and status claims in this file are **historical**. They are NOT
> authoritative and must not be used for engineering decisions, release gating,
> or any customer-facing claim.
>
> **Authoritative source:** [`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md)
>
> Superseded 2026-08-16. Retained only as a record of what was believed at the time.

# PDFEngine Rendering Conformance Matrix

**This document previously reported per-feature test-pass fractions (e.g. "Grid
Alignment — 84/90", "colspan / rowspan — 12/20") mapped to named "Gold templates" and
"Evidence Targets" that do not exist anywhere in this repository.** There is no CSS
Grid/Flexbox/typography conformance suite in this codebase today, so there is nothing
those fractions could have been measured against. This document has been rewritten to
state what's actually known — real capability where it's genuinely implemented,
"Chromium native" where PdfEngine adds no logic of its own on top of the browser's
default behavior, and "Unverified" where no test or inspected render backs a claim.

See `Implementation_Status.md` for full evidence and file:line citations.

---

## 1. Layout (Grid, Flexbox, positioning)

PdfEngine's `LayoutAnalyzer` detects risky CSS patterns (fixed positioning, container
queries, backdrop-filter, etc.) and emits warnings — it does not alter how Chromium
lays these out. All actual grid/flexbox/positioning rendering is Chromium's native
engine, unmodified by PdfEngine. **Status: Chromium-native, unverified by any PdfEngine
test.** If Chromium renders it correctly, PdfEngine's output will too; PdfEngine adds
no layout logic here today.

## 2. Pagination

| Feature | Status | Evidence |
| :--- | :--- | :--- |
| Printable page height from real page size/margins/orientation | Implemented | `PaginationPlanner.ComputePrintableHeightPx`, `PaginationPlannerTests.cs` |
| `@page` CSS honored (size, mixed within limits above) | Implemented | `PreferCSSPageSize` wired into Playwright's `PagePdfOptions` |
| Orphan-heading avoidance (measures next sibling) | Partial | Heuristic, whole-block only — see `PaginationPlanner.cs` |
| Widow/orphan line-level control | Missing | Not implemented |
| Safe mid-paragraph text fragmentation | Missing | Not implemented |
| `page-break-inside: avoid` for tables/lists/images | Implemented | CSS injected per render in `PlaywrightPdfService.cs` |
| Rowspan/colspan-safe table breaking | Missing | Matches open `DEFECT-002` |

## 3. Typography

| Feature | Status | Evidence |
| :--- | :--- | :--- |
| Local font caching, Google Fonts interception | Implemented | `TypographyEngine.cs` |
| Font fallback | Partial | Single fallback to Inter, no coverage-based chain |
| RTL/bidi (Arabic, Hebrew) | Cosmetic | `dir="auto"` + one CSS rule; matches open `DEFECT-001` |
| CJK, Devanagari shaping | Chromium-native, unverified | No PdfEngine-specific logic |
| Glyph coverage checking | Missing | No backing code |
| Embedded font verification | Cosmetic | Cannot detect fonts in compressed PDF streams |

## 4. Vector & Graphics (SVG, Canvas)

Chromium-native. PdfEngine performs no SVG/canvas-specific processing; rendering
quality is whatever Chromium's print pipeline produces. The one known operational risk
(`DEFECT-005`: canvas charts capturing blank on slow workers) remains open — no
explicit wait-for-render strategy exists yet in `RenderingOptions`. Tracked as backlog.

## 5. Tables

| Feature | Status | Evidence |
| :--- | :--- | :--- |
| Repeating `thead`/`tfoot` | Implemented | CSS injected per render |
| `page-break-inside: avoid` on rows | Implemented | Same injection |
| Rowspan/colspan-safe page splitting | Missing | Matches open `DEFECT-002` |
| Nested table handling | Chromium-native, unverified | No PdfEngine-specific logic |

## 6. Print & Page

| Feature | Status | Evidence |
| :--- | :--- | :--- |
| Custom page size (`PageSize`) | Implemented | `RenderingOptions.PageSize`, validated allowlist |
| Custom margins | Implemented | `RenderingOptions.MarginTop/Bottom/Left/Right` |
| Landscape orientation | Implemented (this pass) | `RenderingOptions.Landscape` → `PagePdfOptions.Landscape` |
| Scale | Implemented (this pass) | `RenderingOptions.Scale` → `PagePdfOptions.Scale` |
| Page ranges | Implemented (this pass) | `RenderingOptions.PageRanges` → `PagePdfOptions.PageRanges` |
| `@page`-driven sizing | Implemented (this pass) | `PreferCSSPageSize` |
| Mixed orientation within one document | Missing | Chromium's print API sets orientation per document, not per page |
| Headers/footers | Implemented | `HeaderTemplate`/`FooterTemplate` passed to Playwright |
| Real PDF metadata (Title/Author/Subject/Keywords) | Implemented (this pass) | `ApplyPdfMetadata` via PdfSharpCore, was previously HTML-only and had no effect on the actual PDF |

## 7. Security

| Feature | Status | Evidence |
| :--- | :--- | :--- |
| Private-IP/SSRF blocking | Implemented | DNS-resolution-based `IsIpSafe`, now covers CGNAT/multicast/reserved/6to4 |
| DNS-rebinding (TOCTOU) protection | Implemented (this pass) | Pinned fetch + `route.FulfillAsync` replaces validate-then-`ContinueAsync` |
| HTML sanitization | Implemented (this pass) | Real DOM-based stripping, see `Implementation_Status.md` §1 |
| Rate limiting (all authenticated request types) | Implemented (this pass) | Previously exempted JWT-authenticated requests |
| Seeded credentials scoped to dev only | Implemented (this pass) | Previously seeded unconditionally on every startup |
