> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Capability and status claims in this file are **historical**. They are NOT
> authoritative and must not be used for engineering decisions, release gating,
> or any customer-facing claim.
>
> **Authoritative source:** [`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md)
>
> Superseded 2026-08-16. Retained only as a record of what was believed at the time.

# PDFEngine Subsystems Implementation Status

This matrix reflects the actual, verified state of each subsystem as of the 2026-08-15
reality audit and the fix pass that followed it — not aspirational status. Every entry
below is backed by a file:line citation, a passing test, or an inspected rendered PDF.
No percentage in this document is a test-pass count unless a test suite in this repo
actually produces it.

**Status legend**
- **Implemented** — does what it claims, with test or empirical evidence.
- **Partial** — real and working, but narrower or less complete than "solved."
- **Cosmetic** — code runs and produces output, but has no real effect.
- **Missing** — claimed capability with no backing implementation.

Full audit: see the "PdfEngine Reality Audit" artifact (2026-08-15) for the original
findings this document was corrected against.

---

## ➔ Logical Subsystems Index

### 1. HTML Sanitizer
*   **Source**: [IHtmlSanitizerStage.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Application/Interfaces/IHtmlSanitizerStage.cs), [HtmlSanitizerStage.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/HtmlSanitizerStage.cs)
*   **Status**: **Implemented**
*   **Evidence**: Rewritten on a real DOM parser (`HtmlSanitizer`/AngleSharp) that removes `<script>`, all `on*` event handlers, `javascript:`/`vbscript:` URIs, and disallowed tags/attributes by allowlist. Verified by `HtmlSanitizerStageTests.cs` (script stripping, event-handler stripping, javascript: URI stripping, safe-markup/SVG/data-URI preservation).
*   **Known limitation**: The CSS sanitizer normalizes some values (e.g. named colors → `rgba()`) as a side effect of parsing — cosmetic, not a defect.
*   **Prior state**: this stage previously logged a fabricated "stripped N handlers" message while returning HTML completely unmodified. That has been replaced, not patched over.

### 2. DOM Analyzer
*   **Source**: [DomAnalyzer.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/DomAnalyzer.cs)
*   **Status**: **Partial**
*   **Evidence**: Node-count and depth (>12) diagnostics are real. Unclosed-tag detection now correctly accounts for the standard HTML5 void-element list (`img`, `br`, `input`, `meta`, etc. with or without a trailing `/`) — verified by `DomAnalyzerTests.cs`.
*   **Known limitation**: Still a bracket-depth heuristic, not a real parser — it can still misreport on deeply malformed or exotic markup that a real HTML5 parser would recover from correctly. A full AngleSharp-based structural pass is a candidate follow-up, not done this pass.

### 3. Layout Analyzer
*   **Source**: [LayoutAnalyzer.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/LayoutAnalyzer.cs)
*   **Status**: **Partial (advisory-only)**
*   **Evidence**: Regex/string-based detectors for `backdrop-filter`, `position:fixed`, container queries, etc. are real and run pre-render.
*   **Known limitation**: Findings only append to a warnings list — nothing consumes them to change rendering behavior. Not touched in this fix pass; wiring these into actual rendering decisions is backlog.

### 4. Pagination Planner
*   **Source**: [PaginationPlanner.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/PaginationPlanner.cs)
*   **Status**: **Partial**
*   **Evidence**: The hardcoded `900px` printable-height constant is gone. `ComputePrintableHeightPx` now derives real printable height from `PageSize` + margins + orientation (A4/Letter/Legal/A3/A5/A6/Tabloid/Ledger, all converted at 96dpi, margins parsed from CSS units) — verified by `PaginationPlannerTests.cs` (six tests covering per-size differentiation, margin subtraction, landscape swap, and unknown-size fallback). The orphan-heading heuristic now measures the *next* sibling's height before deciding to break, not just the heading's own height.
*   **Known limitation**: Still whole-block movement, not line-box-aware. No true widow/orphan line counting, no safe mid-paragraph text fragmentation, no rowspan/colspan-aware table breaking. These are the deepest remaining pagination gaps and are tracked as backlog, not claimed as done.

### 5. Typography Engine
*   **Source**: [ITypographyEngine.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Application/Interfaces/ITypographyEngine.cs), [TypographyEngine.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/TypographyEngine.cs)
*   **Status**: **Partial** — not touched in this fix pass.
| Capability | Status |
| :--- | :--- |
| Font Loading | Partial — hardcoded ~20-family allowlist; unrecognized fonts silently fall back to Inter |
| Local Font Cache | Implemented |
| Font Routing | Implemented (Google Fonts interception is real) |
| Embedded Font Verification | Cosmetic — UTF-8 substring search on PDF bytes, cannot detect fonts inside compressed streams |
| Font Fallback | Partial — single fallback to Inter, no coverage-based chain |
| Glyph Coverage | Missing — no backing code |
| Script Shaping / Bidi Order | Cosmetic — `dir="auto"` + one static CSS rule; all real shaping is delegated to Chromium |
*   **Backlog**: real embedded-font/glyph verification (candidate: PdfPig), broader font coverage.

### 6. Asset Optimizer
*   **Source**: [IAssetOptimizerStage.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Application/Interfaces/IAssetOptimizerStage.cs), [AssetOptimizerStage.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/AssetOptimizerStage.cs)
*   **Status**: **Partial (warn-only)** — not touched in this fix pass.
*   **Evidence**: Base64 size inspection and oversized-image warnings are real. The "loading optimization" line is a no-op regex replace (matches and replaces with itself). No actual resize/recompress/enforcement.
*   **Backlog**: real image optimization; enforce (not just warn on) size limits.

### 7. Print Optimizer
*   **Source**: [PlaywrightPdfService.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/PlaywrightPdfService.cs)
*   **Status**: **Implemented**
*   **Evidence**: `RenderingOptions` now exposes `Landscape`, `Scale`, `PageRanges`, `PreferCSSPageSize` (default `true`), all wired into Playwright's `PagePdfOptions`. `@page` CSS is now honored instead of being silently overridden by a fixed Format — previously every PDF was forced to the same A4 box regardless of content; verified by rendering a landscape/custom-margin document and inspecting the output `MediaBox` directly.
*   **Known limitation**: True per-section mixed orientation inside a single PDF (e.g. one landscape page inside an otherwise-portrait document) is not supported — Chromium's print API sets orientation per document, not per page. Achieving that needs either multi-render+merge or CSS paged-media tricks; tracked as backlog.

### 8. Table Subsystem
*   **Source**: [PlaywrightPdfService.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/PlaywrightPdfService.cs)
*   **Status**: **Partial** — not touched in this fix pass.
*   **Evidence**: Repeating `thead`/`tfoot` and `page-break-inside: avoid` CSS injection are real, PdfEngine-specific behavior (not just default browser behavior).
*   **Known limitation**: No rowspan/colspan-aware pagination exists — a spanning cell that straddles a page break will corrupt border alignment. Matches the project's own open `DEFECT-002` in `Rendering_Defects.md`. Tracked as backlog (measurement-driven forced breaks before unsafe spanning rows).

### 9. Resource Loader
*   **Source**: [TypographyEngine.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/TypographyEngine.cs)
*   **Status**: **Implemented (narrow scope)** — not touched in this fix pass.
*   **Evidence**: Local font cache and Google Fonts route interception are genuinely real.
*   **Known limitation**: Coverage is a hardcoded ~20-family allowlist; anything outside it silently substitutes Inter with no warning surfaced.

### 10. Security Engine
*   **Source**: [PlaywrightPdfService.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/PlaywrightPdfService.cs), [Program.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.API/Program.cs), [RateLimitingMiddleware.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.API/Middlewares/RateLimitingMiddleware.cs)
*   **Status**: **Implemented**, materially strengthened this pass.
*   **Evidence**:
    - The DNS-rebinding/TOCTOU gap is closed: outbound resource requests are no longer validated-then-handed-to-Chromium (`route.ContinueAsync()`); they are now fetched by the engine itself over a connection pinned to the pre-validated IP (`SocketsHttpHandler.ConnectCallback`) and returned via `route.FulfillAsync()`, with each redirect hop re-validated independently (capped at 5 hops).
    - `IsIpSafe` now also blocks CGNAT (100.64.0.0/10), 0.0.0.0/8, multicast/reserved ranges, and unwraps deprecated IPv4-compatible and 6to4 IPv6 addresses before checking them, instead of trusting any non-mapped IPv6 address by default.
    - Byte-limit enforcement happens during the pinned fetch itself, not just after the fact from response headers.
    - Rate limiting no longer exempts JWT-authenticated requests — it previously skipped all of them due to a stray condition; now every request that resolves a tenant is limited.
    - The seeded admin account (`admin@example.com` / `password123`) and fixed test API key (`test-api-key-123`) now only seed in the `Development` environment, not unconditionally on every startup.
*   **Known limitation**: This protects subresource fetches triggered by the rendered HTML (images, stylesheets, scripts, XHR/fetch) — the current architecture loads the primary document via `page.SetContentAsync`, not a URL navigation, so there is no top-level navigation SSRF surface today. If URL-based rendering is added later, it must go through the same pinned-fetch path.

### 11. Browser Orchestrator
*   **Source**: [BrowserManager.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/BrowserManager.cs)
*   **Status**: **Implemented**, description corrected.
*   **Evidence**: Recycle-after-50-renders and grace-period disposal are real, in-process behavior.
*   **Correction**: earlier documentation described this as "worker clusters" and "monitoring daemons" — it is a single in-process counter-based restart, not a fleet. Language corrected to match reality; behavior unchanged.

### 12. Verification Engine
*   **Source**: [PlaywrightPdfService.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/PlaywrightPdfService.cs)
*   **Status**: **Cosmetic** — not touched in this fix pass.
*   **Evidence**: `ComputeVisualDrift` is a byte-for-byte raw PNG comparison, meaningless for a compressed format, and only runs if a caller supplies a reference image — nothing in the repo does. "RenderScore" is an internally-computed heuristic, not certification against any ground truth.
*   **Backlog**: real pixel-level diff (candidate: SkiaSharp, Apache-2.0) against an actual golden-image store.

### 13. PDF Metadata *(new this pass)*
*   **Source**: [PlaywrightPdfService.cs](file:///Users/saddamkhanlavani/dotnet/PdfEngine/src/PdfEngine.Infrastructure/Services/PlaywrightPdfService.cs) — `ApplyPdfMetadata`
*   **Status**: **Implemented**
*   **Evidence**: `Title`/`Author`/`Subject`/`Keywords` on `RenderingOptions` previously only reached the HTML `<head>` as `<meta>` tags, which no PDF reader treats as document metadata. A post-process step now writes the real PDF `/Info` dictionary via `PdfSharpCore`. Verified by `PdfMetadataTests.cs` (round-trip write/read of Title/Author/Subject/Keywords).
