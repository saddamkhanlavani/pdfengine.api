> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Rendering Specification Contract

This document outlines the layout, print, and script rules supported by PDFEngine. All engine improvements must compile in conformance with this specification.

---

## 1. Tables Subsystem Specifications
*   **Header and Footer Repeating**: `thead` and `tfoot` must repeat on split page boundaries. Author templates should include recommended CSS guidelines:
    ```css
    @media print {
        thead { display: table-header-group !important; }
        tfoot { display: table-footer-group !important; }
    }
    ```
    *Engine Goal*: In the long term, the Table Engine must natively handle dynamic rowspan height splits and prevent subsequent border alignment errors, independent of template CSS attributes.
*   **Column Width Calculations**: Recommend `table-layout: fixed` width percentages on tabular templates to prevent dynamic column shrink defects.
*   **Page Splitting**: Row elements must not break mid-line. In addition to CSS rules (`tr { page-break-inside: avoid; }`), the Table Engine must programmatically check row boundaries inside DOM/Layout Analyzer passes.

---

## 2. Pagination Subsystem Specifications
*   **Widow & Orphan Management**: Minimum lines left at bottom/top of page blocks is set to **2 lines**. The Pagination Planner must dynamically balance layout block spacing.
*   **Avoid Heading Orphans**: Prevent headings (`h1`, `h2`, `h3`) from sitting alone at page bottom boundaries. In addition to CSS hints (`page-break-after: avoid;`), the engine must dynamically inspect bounding offsets and push orphaned headings to the next page layout.
*   **Page Balancing**: Content heights spanning less than 40% of the printable area should trigger margin-balancing routines to distribute spacing evenly.

---

## 3. Typography Subsystem Specifications
*   **Local Webfont Caching**: Outbound calls to Google Fonts or gstatic directories must be intercepted and served locally from the server's subset cache.
*   **Bidirectional RTL Text**: Multi-script strings (e.g. Arabic mixed with English inside a table cell) must evaluate bounds using logical line reordering.
*   **CJK Text Flows**: Supports Japanese vertical flow writing modes using `writing-mode: vertical-rl` and `text-orientation: mixed`.

---

## 4. Security Subsystem Specifications
*   **DNS Resolution Interceptor**: Outbound calls targeting private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) or loopback ranges must be resolved and blocked to prevent SSRF vulnerabilities.
*   **Script Timeouts**: Max execution limit for inline javascript scripts (e.g., Chart.js, D3 graphs) is capped at **3000ms**.
