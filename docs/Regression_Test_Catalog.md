> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Capability and status claims in this file are **historical**. They are NOT
> authoritative and must not be used for engineering decisions, release gating,
> or any customer-facing claim.
>
> **Authoritative source:** [`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md)
>
> Superseded 2026-08-16. Retained only as a record of what was believed at the time.

# PDFEngine Regression Test Catalog

This catalog indexes all layout, typographic, vector, and security regression test cases. Every bug fixed must possess an entry here to prevent future functional regressions.

---

## ➔ Active Test Registry

### Test ID: `REG-001`
*   **Feature Checked**: Table Column Wrapping
*   **Description**: Ensures that long text in table description columns does not wrap vertically word-by-word, but maps horizontally.
*   **Test Template File**: `tests/certification/invoice/template.html`
*   **Expected PDF Output**: `tests/Evidence/invoice/invoice.pdf` (Page 1)
*   **Fidelity Status**: **PASS**

### Test ID: `REG-002`
*   **Feature Checked**: RTL Bidirectional script shaping
*   **Description**: Verifies that mixed Arabic and English text renders in correct character sequencing without reverse alignments inside table cells.
*   **Test Template File**: `tests/certification/ultimate/template.html` (#sec-rtl-15)
*   **Expected PDF Output**: `tests/Evidence/ultimate/ultimate.pdf` (Page 15)
*   **Fidelity Status**: **PASS**

### Test ID: `REG-003`
*   **Feature Checked**: Canvas charts rendering delays
*   **Description**: Ensures Chart.js and D3 canvas graphs finish active draw cycles before Playwright triggers the PDF snapshot.
*   **Test Template File**: `tests/certification/dashboard/template.html`
*   **Expected PDF Output**: `tests/Evidence/dashboard/dashboard.pdf` (Page 2)
*   **Fidelity Status**: **PASS**

### Test ID: `REG-004`
*   **Feature Checked**: SSRF Local Loopback interception
*   **Description**: Asserts that requests targeting internal loopbacks (`127.0.0.1`, `localhost`) and private ranges are aborted.
*   **Test Script**: `tests/generate_all_evidence.js` (Asset failure checks)
*   **Expected PDF Output**: `tests/Evidence/Asset_Failure_Diagnostics.json`
*   **Fidelity Status**: **PASS**

### Test ID: `REG-005`
*   **Feature Checked**: Offline local font subsets loading
*   **Description**: Ensures that gstatic webfont requests are intercepted and loaded using local `.ttf` assets when the engine has no internet access.
*   **Test Template File**: `tests/certification/invoice/template.html`
*   **Expected PDF Output**: `tests/Evidence/invoice/invoice.pdf` (Embedded Fonts)
*   **Fidelity Status**: **PASS**

### Test ID: `REG-006`
*   **Feature Checked**: Orphaned headings page separation
*   **Description**: Verifies that headings (`h1`, `h2`, `h3`) do not sit alone at the bottom of pages.
*   **Test Template File**: `tests/certification/annual_report/template.html`
*   **Expected PDF Output**: `tests/Evidence/annual_report/annual_report.pdf` (Page 4)
*   **Fidelity Status**: **FAIL** (Backlog `RENDERER-0003`)
