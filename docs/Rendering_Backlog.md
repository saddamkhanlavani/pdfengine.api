> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Planning/checklist content in this file is **historical** and may contradict the
> current roadmap and release gates.
>
> **Authoritative sources:** [`PDFENGINE_TARGET_ARTIFACT.md`](./PDFENGINE_TARGET_ARTIFACT.md)
> and [`PDFENGINE_RELEASE_GATES.md`](./PDFENGINE_RELEASE_GATES.md)
>
> Superseded 2026-08-16. Retained as a historical record.

# PDFEngine Rendering Backlog

This backlog tracks the active layout, typography, pagination, and print defects that must be resolved to achieve world-class rendering quality.

---

## ➔ Active Backlog Items

### ID: `RENDERER-0001`
*   **Title**: Mixed RTL Arabic and LTR English inside table columns renders in reverse order.
*   **Priority**: **Critical**
*   **Customer Impact**: High (Corrupts multi-language invoices, contracts, and shipping notes).
*   **Evidence Target**: `tests/results_quality_certification.json` (template: `arabic_rtl`)
*   **Subsystem**: Typography Engine
*   **Known Root Cause**: Webkit/Blink layout runs reverse text direction parsing before cell constraints are computed.
*   **Proposed Resolution**: Force explicit `dir="auto"` parameters and Noto Sans Arabic subset mapping on cells.
*   **Status**: **Open**

---

### ID: `RENDERER-0002`
*   **Title**: Table rowspan cells split awkwardly across page breaks, cutting text.
*   **Priority**: **High**
*   **Customer Impact**: High (Causes missing lines and overlapping borders in long tables).
*   **Evidence Target**: `tests/results_quality_certification.json` (template: `soc2`)
*   **Subsystem**: Table Subsystem
*   **Known Root Cause**: PDF print compositor does not calculate height metrics for rowspan cells spanning multiple pages.
*   **Proposed Resolution**: Pre-calculate row heights in a DOM analyzer pass and split rows programmatically.
*   **Status**: **Open**

---

### ID: `RENDERER-0003`
*   **Title**: Headings stand alone at the bottom of pages (Orphaned Headers).
*   **Priority**: **High**
*   **Customer Impact**: Medium (Disrupts reading flow and leaves unnecessary white space at the bottom of pages).
*   **Evidence Target**: `tests/results_quality_certification.json` (template: `annual_report`)
*   **Subsystem**: Pagination Planner
*   **Known Root Cause**: Playwright's PDF print pipeline does not check height remaining on current page before printing heading blocks.
*   **Proposed Resolution**: Implement a preflight page-breaking planner script to check block heights and push orphans to the next page.
*   **Status**: **Open**

---

### ID: `RENDERER-0004`
*   **Title**: Sticky position header elements overlap table content on second page.
*   **Priority**: **Medium**
*   **Customer Impact**: Medium (Fails to lock headers correctly during print actions).
*   **Evidence Target**: `tests/results_quality_certification.json` (template: `soc2`)
*   **Subsystem**: Layout Analyzer
*   **Known Root Cause**: CSS `position: sticky` is designed for interactive viewports; during print compilation, it overlays content boxes if page heights shift.
*   **Proposed Resolution**: Normalise styles by deprecating sticky positions in print media queries.
*   **Status**: **Open**

---

### ID: `RENDERER-0005`
*   **Title**: Chart.js graphs capture blank canvas snapshots on slow workers.
*   **Priority**: **High**
*   **Customer Impact**: High (Dashboard charts render blank under heavy server concurrent loads).
*   **Evidence Target**: `tests/results_quality_certification.json` (template: `dashboard`)
*   **Subsystem**: Canvas Engine
*   **Known Root Cause**: PDF compilation triggers before Chart.js canvas draw cycles finish due to animation lag.
*   **Proposed Resolution**: Disable animations in dashboard templates to guarantee immediate static layouts.
*   **Status**: **Open**
