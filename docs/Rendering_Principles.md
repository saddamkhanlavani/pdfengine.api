> ## ℹ️ REFERENCE / HISTORICAL
>
> This file is architecture, principles, requirements or a decision log — not a
> capability claim. Where it states or implies a capability status, the
> [capability registry](./PDFENGINE_CAPABILITY_REGISTRY.md) overrides it.

# PDFEngine Rendering Principles (Constitution)

PDFEngine exists to maximize HTML-to-PDF fidelity. Every architectural decision, optimization, and feature must improve rendering correctness, standards compliance, diagnosability, security, or customer success. Cosmetic changes, documentation-only updates, and unsupported claims never satisfy a roadmap item.

---

## ➔ Engineering Core Tenets

### 1. Documentation Never Satisfies a Requirement
A feature or backlog issue is only considered resolved when:
*   An implementation exists in the source files.
*   Unit and integration tests pass successfully.
*   Automated PDF evidence has been compiled.
*   Relevant status and specifications documents are updated.

### 2. Evidence Before Claim
No rendering capability or CSS conformance level may be claimed without:
*   A dedicated regression test case under `tests/regression/`.
*   A generated visual PDF rendering stored in the test output directories.
*   Programmatic checkmarks verified by the pipeline runner.

### 3. Every Defect Becomes Engineering Work
Every reported layout defect, text wrap error, or page break bug must:
1.  Be assigned a unique ID in the defect log.
2.  Generate a regression test that fails under the unfixed engine code.
3.  Be resolved by refining the pre-rendering or print optimizer engine.
4.  Be checked in the continuous integration runner to prevent regressions.

### 4. Never Hide Layout Defects
Never solve whitespace voids, overlap bugs, or text wrapping errors by injecting cosmetic margins or mock placeholder paragraphs into test templates. Spacing errors are layout defects; they must be resolved by fixing the **Pagination Planner** and **Layout Analyzer** engines.

### 5. Browser is Not the Renderer
Headless browsers (Playwright/Chromium) are simply printers. They receive a **RenderPlan** and compile pixel streams. The core rendering intelligence (measuring DOM depth, predicting overflows, planning page-breaks, shaping fonts, and sanitizing assets) must reside in C# pre-rendering compiler stages.

### 6. Subsystem Bug Ownership
Every layout bug, script warning, or execution timeout must be tracked directly against its owning subsystem (Layout, Pagination, Typography, Tables, Vector, Canvas, Forms, Print, Resource Loader, Security, Verification). A subsystem is not considered stable if it possesses open critical bugs.

### 7. Standards-Compliant Rendering
The engine should faithfully render standards-compliant HTML and CSS. When input is invalid or relies on undefined browser behavior, the engine should produce deterministic results, document the limitation, and emit actionable diagnostics rather than silently changing the content.

### 8. No Manual Overrides
The layout compiler must run programmatically on all inputs. The engine must never require developers or template authors to apply custom patches or manually adjust widths to pass conformance targets.

### 9. Conformance Over Marketing
Never advertise "100% W3C standard coverage." Conformance status must always be reported as the count of verified tests passed against the total test suite capacity.
