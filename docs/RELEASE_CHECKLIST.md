> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Planning/checklist content in this file is **historical** and may contradict the
> current roadmap and release gates.
>
> **Authoritative sources:** [`PDFENGINE_TARGET_ARTIFACT.md`](./PDFENGINE_TARGET_ARTIFACT.md)
> and [`PDFENGINE_RELEASE_GATES.md`](./PDFENGINE_RELEASE_GATES.md)
>
> Superseded 2026-08-16. Retained as a historical record.

# PDFEngine Release Gate Checklist

This document details the checklist requirements that must be met prior to releasing PDFEngine updates to production. No release cycle can bypass these gates.

---

## 1. Conformance Gate Requirements

*   `[ ]` **Layout Conformance**: CSS Grid and Flexbox test suites must achieve a **>95% pass rate** (Grid: 86/90 minimum | Flex: 61/64 minimum).
*   `[ ]` **Pagination Conformance**: Widow/orphan checks and page-break rules must achieve a **>80% pass rate** (Pagination: 70/88 minimum).
*   `[ ]` **Typography Conformance**: Script shaping, RTL, and CJK vertical tests must achieve a **>90% pass rate** (Typo: 50/55 minimum).
*   `[ ]` **Table Conformance**: Rowspan splitting and header repeats must achieve a **>90% pass rate** (Tables: 165/183 minimum).

---

## 2. Quality & Correctness Gates

*   `[ ]` **Page Utilization check**: Zero pages in the reference corpus have page utilization rates under 40% (except final pages of sections).
*   `[ ]` **Orphaned Headings check**: Zero orphaned headings (headings placed at page bottoms without text).
*   `[ ]` **OCR Read check**: 100% text extraction matching (no missing glyphs or scrambled words).
*   `[ ]` **Barcode / QR check**: All generated document barcodes parse and decode successfully.
*   `[ ]` **Embedded Fonts check**: Auditing binaries confirms all custom fonts are embedded (`Embedded: True`).

---

## 3. Capacity & Performance Gates

*   `[ ]` **P95 Render Duration**: Under **500ms** for 10-page files.
*   `[ ]` **P99 Render Duration**: Under **3000ms** for 1000-page files.
*   `[ ]` **Peak Worker Memory**: Headless browser RAM stays under **256MB** during loop testing.
*   `[ ]` **GC Sweeps recovery**: Memory blocks clear within 200ms of request completions.

---

## 4. Reports Compilation

*   `[ ]` **Consolidated PDFs Created**: Run evidence compilers to generate:
    *   `Executive_Evidence_Book.pdf`
    *   `Executive_Report.pdf`
    *   `Release_Certificate.pdf`
*   `[ ]` **Maturity Matrices Updated**: Verify `Rendering_Conformance_Matrix.md` and `Feature_Status.md` are updated to match recent test evidence.
