> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Renderer Knowledge Base

This document contains architectural lessons, layout hacks, and gotchas discovered during the development and maintenance of the Playwright PDF rendering engine.

---

## ➔ Engineering Lessons Learned

### Lesson 1: Column Shrink-Wrapping Defect
*   **Context**: Text elements in table columns wrap vertically, letter-by-letter (like `D\ne\ns\nc\nr...`).
*   **Root Cause**: Table layout auto-calculation shrinks columns dynamically to make room for numeric columns.
*   **Solution**: Set `table-layout: fixed` on the table and assign explicit percentage width style definitions to text columns.
    ```css
    table { table-layout: fixed; width: 100%; }
    .col-desc { width: 50%; word-wrap: break-word; }
    ```

### Lesson 2: Page Break Cutting Rows
*   **Context**: Table rows or paragraphs split across page margins, cutting text lines in half.
*   **Root Cause**: Browser default printing engine slices block elements at exact pixel heights.
*   **Solution**: Apply page-break control parameters in print stylesheets:
    ```css
    tr, blockquote, pre { page-break-inside: avoid !important; }
    h1, h2, h3 { page-break-after: avoid !important; }
    ```

### Lesson 3: Chart.js Rendering Blank Canvas
*   **Context**: Graphs are compiled blank inside target PDFs under high concurrent server loads.
*   **Root Cause**: Playwright prints the page before canvas animation routines complete.
*   **Solution**: Set `animation: false` or speed values to 0 in Chart.js options, and wait for canvas drawing completion before triggering PDF generation.

### Lesson 4: SSRF DNS Rebinding Threat
*   **Context**: Standard URL fetching can be spoofed using DNS Rebinding (where a host resolves to a safe IP first, then changes to an internal IP).
*   **Solution**: Cache resolved IP addresses locally during the request cycle (`DnsCache`) and enforce immediate socket resolution intercepts.

### Lesson 5: Font Layout Shifting
*   **Context**: Fonts take >2s to download, causing text width shifts that push lines to new pages.
*   **Solution**: Intercept Google Fonts API calls and serve local `.ttf`/`.woff2` subsets directly from the server's cache directory. Declare local font fallbacks in stylesheets.
