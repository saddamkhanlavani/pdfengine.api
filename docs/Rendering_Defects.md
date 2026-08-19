> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Capability and status claims in this file are **historical**. They are NOT
> authoritative and must not be used for engineering decisions, release gating,
> or any customer-facing claim.
>
> **Authoritative source:** [`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md)
>
> Superseded 2026-08-16. Retained only as a record of what was believed at the time.

# PDFEngine Rendering Defects Registry

This document records the active rendering bugs, layout errors, and pagination issues identified during conformance testing.

---

## ➔ Active Defects Registry

### Bug ID: `DEFECT-001`
*   **Title**: Mixed Arabic + English inside table columns renders in reverse order.
*   **Found Date**: 2026-07-15
*   **Subsystem**: Typography Engine
*   **Root Cause**: Browser default text rendering engine wraps mixed direction text inconsistently when widths are constrained inside grid-cells.
*   **Fidelity Score Impact**: High (Fails multi-language statement rendering).
*   **Status**: **Open**
*   **Resolution Plan**: Configure explicit `dir="auto"` parameters and Noto Arabic font fallback matrices for text columns containing multi-script properties.

---

### Bug ID: `DEFECT-002`
*   **Title**: Table rowspan cell overflow cutting text.
*   **Found Date**: 2026-07-15
*   **Subsystem**: Table Subsystem
*   **Root Cause**: Playwright's print compositor does not calculate height metrics for rowspan cells spanning multiple pages, leading to overlapping borders.
*   **Fidelity Score Impact**: High (Corrupts tabular audit logs).
*   **Status**: **Open**
*   **Resolution Plan**: Develop a DOM analyzer pre-pass script that calculates rowspan cell heights and segments them before rendering page buffers.

---

### Bug ID: `DEFECT-003`
*   **Title**: Headings stand alone at the bottom of pages (Orphaned Headers).
*   **Found Date**: 2026-07-15
*   **Subsystem**: Pagination Planner
*   **Root Cause**: PDF print pipeline does not check remaining space on a page before rendering heading block components.
*   **Fidelity Score Impact**: Medium (Leaves large whitespaces at the bottom of pages).
*   **Status**: **Open**
*   **Resolution Plan**: Inject a CSS print rule mapping `h1, h2, h3 { page-break-after: avoid; }`, and calculate remaining page height before layout passes.

---

### Bug ID: `DEFECT-004`
*   **Title**: Sticky position header elements overlap table content on second page.
*   **Found Date**: 2026-07-15
*   **Subsystem**: Layout Analyzer
*   **Root Cause**: CSS `position: sticky` is designed for interactive viewports; during print compilation, it overlays content boxes if page heights shift.
*   **Fidelity Score Impact**: Medium (Fails table scroll lock actions).
*   **Status**: **Open**
*   **Resolution Plan**: Deprecate sticky headers in print media queries, replacing them with standardized repeating `thead` layouts.

---

### Bug ID: `DEFECT-005`
*   **Title**: Chart.js graphs capture blank canvas snapshots on slow workers.
*   **Found Date**: 2026-07-15
*   **Subsystem**: Canvas Engine
*   **Root Cause**: Playwright prints the page before canvas animation routines complete.
*   **Fidelity Score Impact**: High (Blank chart blocks in PDF outputs).
*   **Status**: **Open**
*   **Resolution Plan**: Ensure animations are disabled (`options.animation = false`) inside template configurations to ensure static layouts resolve instantly.
