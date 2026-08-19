> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Customer Request & Layout Catalog

This catalog tracks HTML layouts submitted by customers, rendering bugs identified during audits, and the resulting engineering tasks triggered to fix them.

---

## ➔ Customer Layout Registry

### Customer ID: `CUST-001` (Global Tech Solutions)
*   **Layout Type**: Multi-page Billing Invoice
*   **HTML Sample**: `tests/certification/invoice/template.html`
*   **Identified Bug**: Description column text wrapped vertically letter-by-letter.
*   **Root Cause**: Table layout auto-calculation shrinks columns dynamically to make room for numeric columns.
*   **Subsystem Trigger**: Table Subsystem
*   **Resolution Status**: **CLOSED** (Resolved by enforcing `table-layout: fixed` and inline width controls in the engine templates. Ref: `REG-001`).

---

### Customer ID: `CUST-002` (Apex Health Systems)
*   **Layout Type**: Patient Diagnostics Lab Sheet
*   **HTML Sample**: `tests/certification/healthcare/template.html`
*   **Identified Bug**: Pages 3 to 18 left large blank whitespace voids in the bottom half.
*   **Root Cause**: Ingress layout failed to expand to fill printable boundaries under short text paragraphs.
*   **Subsystem Trigger**: Pagination Planner
*   **Resolution Status**: **OPEN** (Temporary mitigation: template adjustment; Engine status: still OPEN until Pagination Planner resolves it).

---

### Customer ID: `CUST-003` (Yellow Express Logistics)
*   **Layout Type**: CN22 Customs Declaration Sheet
*   **HTML Sample**: `tests/certification/shipping/template.html`
*   **Identified Bug**: PDF landscape orientations split margins during mixed orientation compilation.
*   **Root Cause**: Playwright's orientation metadata does not dynamically swap @page bounds if target margins are explicitly set in px styles.
*   **Subsystem Trigger**: Print & Page Subsystem
*   **Resolution Status**: **CLOSED** (Resolved by normalizing page size limits to standard points/mm dimensions).
