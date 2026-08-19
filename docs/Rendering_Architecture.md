> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Rendering Architecture & Subsystems

This document describes the architectural flow, component interactions, and processing subsystems of PDFEngine.

---

## 1. The Rendering Pipeline

To produce professional, print-ready documents, the rendering engine processes input HTML through an intelligent multi-stage pipeline:

```
[HTML Payload] ➔ [HTML Parser] ➔ [DOM Analyzer] ➔ [Layout Analyzer] ➔ [Pagination Planner] ➔ [Typography Engine] ➔ [Asset Optimizer] ➔ [Playwright] ➔ [PDF]
```

### A. HTML Parser
Validates input tags, corrects structural syntax errors (such as unclosed tags), and screens the document for template placeholder tags (like `{{variable}}` or `${variable}`).

### B. DOM Analyzer
Parses the DOM tree to measure depth, overall node count, and weight. It flags high-risk structures (e.g. DOM tree depth > 12) that could cause memory bottlenecks or infinite loop risks.

### C. Layout Analyzer
Runs before browser layout triggers to evaluate bounding boxes, absolute position variables, and overflow properties.
*   **Key Checks**: Identifies elements with `overflow: hidden` where text content exceeds client boundaries, preventing text cutting.

### D. Pagination Planner
Computes page breaks, widow/orphan bounds, and balances blocks to prevent excessive whitespace at the bottom of pages.
*   **Keep-with-next**: Ensures heading blocks do not stand alone at page boundaries by calculating block offsets.
*   **Keep-together**: Ensures that small tables and lists stay grouped on a single page.

### E. Typography Engine
Enforces correct script shaping, variable font weights mapping, and RTL/LTR mixed directions.
*   **Google Fonts Intercept**: Reroutes remote stylesheet requests to local caches to prevent font shifts caused by network latency.
*   **Script Shaping**: Utilizes HarfBuzz rules inside headless browser instances to shape RTL Arabic, Hebrew, and Devanagari text correctly.

### F. Asset Optimizer
Normalizes external assets (images, stylesheets, scripts) prior to rendering:
*   Scales vector SVGs to bounding boxes.
*   Converts mixed HTTP resources to secure HTTPS calls.
*   Blocks unsafe loopback resources to prevent SSRF vulnerabilities.

---

## 2. Shared Context Pooling

To optimize system memory and speed targets, Playwright browser instances are managed in a thread-safe pool:
*   **Shared Contexts**: Standard rendering requests run inside shared browser context sheets. Spawning pages inside a shared context minimizes the context startup overhead.
*   **HAR Contexts**: Spawned on-demand when troubleshooting network or stylesheet lag. Captures complete HTTP logs inside temporary HAR files.
*   **Automatic Recycling**: Spawns monitoring daemons that automatically restart worker browser clusters if a crash or memory leak is detected.

---

## 3. Logical Code Boundaries

To maintain clean modular boundaries, the code is structured into four logical subsystems:
1.  **`PdfEngine.Core`**: Pre-render compiler logic (HTML/CSS parsing, DOM analysis, layout metrics, pagination planning, and typography models).
2.  **`PdfEngine.Runtime`**: Isolated execution backend (Playwright cluster processes, resource route intercepts, and cache handlers).
3.  **`PdfEngine.API`**: Public endpoints routing and request middleware filters.
4.  **`PdfEngine.Platform`**: Tenant administration, Stripe webhook processors, and metrics dashboards.

> [!NOTE]
> These represent logical architectural boundaries. They may initially exist as namespaces/folders within the solution and be extracted into separate physical projects (.csproj) only when the implementation justifies the split.

