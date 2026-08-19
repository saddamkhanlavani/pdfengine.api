> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Planning/checklist content in this file is **historical** and may contradict the
> current roadmap and release gates.
>
> **Authoritative sources:** [`PDFENGINE_TARGET_ARTIFACT.md`](./PDFENGINE_TARGET_ARTIFACT.md)
> and [`PDFENGINE_RELEASE_GATES.md`](./PDFENGINE_RELEASE_GATES.md)
>
> Superseded 2026-08-16. Retained as a historical record.

# PDFEngine Rendering Roadmap & Maturity Model

Our objective is to build the most predictable, diagnosable, and production-ready HTML-to-PDF rendering service for modern web applications. Every rendering limitation must either be eliminated through engineering or explicitly documented with reproducible evidence. The engine must improve through measurable conformance tests rather than cosmetic adjustments to templates or reports.

This document details the master **Rendering Maturity Model** divided into 12 pipeline stages.

---

## ➔ Architecture Pipeline Overview

PDFEngine does not simply pass HTML to a headless browser. High-fidelity print pagination requires processing layout rules before browser compilation:

```
[HTML Source Input]
        │
        ▼
[1. HTML Parser] (Validates markup, syntax errors, nested tags sanity)
        │
        ▼
[2. DOM Analyzer] (Maps DOM tree depth, element counts, risk profiles)
        │
        ▼
[3. Layout Analyzer] (Detects overflow bounds, absolute conflicts, grid overlaps)
        │
        ▼
[4. Pagination Planner] (Calculates page breaks, widow/orphan bounds, balances)
        │
        ▼
[5. Typography Engine] (Resolves font fallback families, shapes RTL/Devanagari text)
        │
        ▼
[6. Asset Optimizer] (Compresses images, scales vectors, routes local font files)
        │
        ▼
[7. Print Optimizer] (Normalizes page media print style rules)
        │
        ▼
[8. Table Subsystem] (Manages spanning, repeats thead, splits rows gracefully)
        │
        ▼
[9. Resource Loader] (Orchestrates cache hooks, offline retries, timeout gates)
        │
        ▼
[10. Security Engine] (Blocks SSRF, restricts loopback, sandbox bounds)
        │
        ▼
[11. Playwright Cluster] (Compiles PDF pages synchronously or asynchronously)
        │
        ▼
[12. Verification Engine] (OCR reads, barcode parses, outline/hyperlink checks)
```

---

## ➔ Subsystem Development Roadmap

### Phase 1 — HTML Parser & Sanitizer
*   **Goal**: Ensure input HTML is parsed into clean, standard compliance blocks prior to rendering.
*   **Status**: **VERIFIED**
*   **Completion Criteria**:
    *   Unclosed tag corrections.
    *   Template syntax detection warning gates.

### Phase 2 — DOM Analyzer
*   **Goal**: Evaluate DOM tree density, identifying structures that could trigger memory leaks or rendering timeouts.
*   **Status**: **IN PROGRESS**
*   **Completion Criteria**:
    *   Calculates a dynamic element count metric.
    *   Generates warning logs if DOM depth exceeds 12 layers.

### Phase 3 — Layout Analyzer
*   **Goal**: Programmatically inspect bounding boxes, element positioning, and overlapping layout components.
*   **Status**: **IN PROGRESS**
*   **Completion Criteria**:
    *   Scans elements to detect CSS `overflow: hidden` conflicts that clip print layouts.
    *   Detects absolute or fixed elements positioned outside viewports.

### Phase 4 — Pagination Planner
*   **Goal**: Implement commercial-grade pagination rules to eliminate excessive whitespace.
*   **Status**: **PLANNED (High Priority)**
*   **Completion Criteria**:
    *   Widow/orphan paragraph line controls (minimum 2 lines kept together).
    *   Keep-with-next checks to prevent headings from separating from target sections.
    *   Page balancing rules that optimize spacing in the bottom half of pages.

### Phase 5 — Typography Engine
*   **Goal**: Support Variable Fonts, bidirectional text shaping, and correct character glyph mappings.
*   **Status**: **IN PROGRESS**
*   **Completion Criteria**:
    *   HarfBuzz script shaping for mixed RTL Arabic/English.
    *   Devanagari conjunct ligatures alignment checks.
    *   CJK vertical orientation text flow constraints.

### Phase 6 — Asset Optimizer
*   **Goal**: Prepare image, vector, and stylesheet assets for PDF packaging.
*   **Status**: **VERIFIED**
*   **Completion Criteria**:
    *   Vector SVGs scaled correctly to bounding containers.
    *   Mixed-HTTP protocol references resolved to secure paths.

### Phase 7 — Print Optimizer
*   **Goal**: Normalize stylesheets for physical print sizes, resolving CSS media overrides.
*   **Status**: **VERIFIED**
*   **Completion Criteria**:
    *   Enforces standard `@page { size: A4; margin: 12mm; }` bounds if unspecified.

### Phase 8 — Table Subsystem
*   **Goal**: Render dense datasets across page boundaries without splitting row elements or corrupting cell borders.
*   **Status**: **PLANNED**
*   **Completion Criteria**:
    *   Repeat `thead` and `tfoot` headers.
    *   Programmatic split rules for cells containing multi-line rowspans.

### Phase 9 — Resource Loader
*   **Goal**: Manage connection pooling, fetch retries, local caches, and timeout bounds.
*   **Status**: **VERIFIED**

### Phase 10 — Security Engine
*   **Goal**: Prevent SSRF attacks, isolate browser workers, and enforce sandbox resource boundaries.
*   **Status**: **VERIFIED**

### Phase 11 — Rendering Pipeline (Playwright)
*   **Goal**: Manage worker contexts, process recycles, and async job queues.
*   **Status**: **VERIFIED**

### Phase 12 — Verification Engine
*   **Goal**: Programmatically verify output correctness against baseline expectations.
*   **Status**: **IN PROGRESS**
*   **Completion Criteria**:
    *   Automated OCR text validation checks, barcode scan checks, bookmarks checks.
