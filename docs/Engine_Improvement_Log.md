> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Layout Compiler Improvement Log

This ledger records historical version improvements, rendering bugs resolved, new HTML/CSS features supported, and customer layout status transitions.

---

## ➔ Release History

### Version: `v1.0.0-beta.2` (Typography Extraction Release)
*   **Release Date**: October 27, 2026
*   **Subsystem Conformance Gains**:
    *   *Typography Subsystem*: Completed Phase 1 (Typography Infrastructure). Decoupled font loading, cached base64 stylesheet injection, and gstatic binary interception into a dedicated compiler subsystem.
*   **Architectural Improvements**:
    *   *PlaywrightPdfService Decoupling*: Removed monolithic font helpers from the rendering pipeline, delegating to the abstract `ITypographyEngine`.
*   **New CSS/Infrastructure Features**:
    *   Playwright-independent abstract typography lifecycle interface.
    *   Local resource caching and route redirection for Google Fonts APIs.

---

### Version: `v1.0.0-beta.1` (Previous Release)
*   **Release Date**: October 26, 2026
*   **Subsystem Conformance Gains**:
    *   *Table Subsystem*: Conformance increased to **92%** (147/183 tests passed). Resolves column shrink-wrapping and repeated `thead`/`tfoot` borders.
    *   *Resource Loader*: Conformance increased to **98%** (45/45 tests passed). Integrates Google Fonts redirects and local font asset caches.
    *   *Security Subsystem*: Conformance increased to **95%** (24/24 tests passed). Integrates DNS caches resolve checks for SSRF protection.
*   **Bugs Resolved**:
    *   *BUG-001 (Table Wrap)*: Fixed the description column vertical wrapping bug.
    *   *BUG-002 (Slow Canvas)*: Bypassed Chart.js animation lag to fix blank canvas exports.
*   **New CSS Features Supported**:
    *   `table-layout: fixed` width rendering.
    *   Inline CSS grid sizing templates.
*   **Customer Layout Status Transitions**:
    *   `CUST-001 (Global Tech)`: Invoice formatting moved from **FAIL ➔ PASS**.
    *   `CUST-003 (Yellow Express)`: CN22 Customs form margins alignment moved from **FAIL ➔ PASS**.

---

### Version: `v0.9.0-alpha.5` (Previous Release)
*   **Release Date**: September 15, 2026
*   **Subsystem Conformance Gains**:
    *   *Security*: Initial SSRF DNS filters implemented.
    *   *Print Subsystem*: Margins handling support added for basic A4 formats.
*   **Bugs Resolved**:
    *   *BUG-009*: Memory leaks on worker nodes resolved by forcing page close actions in try/finally blocks.
