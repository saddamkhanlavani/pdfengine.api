> ## ⛔ SUPERSEDED — DO NOT USE AS SOURCE OF TRUTH
>
> Planning/checklist content in this file is **historical** and may contradict the
> current roadmap and release gates.
>
> **Authoritative sources:** [`PDFENGINE_TARGET_ARTIFACT.md`](./PDFENGINE_TARGET_ARTIFACT.md)
> and [`PDFENGINE_RELEASE_GATES.md`](./PDFENGINE_RELEASE_GATES.md)
>
> Superseded 2026-08-16. Retained as a historical record.

# PDFEngine Rendering Verification Guide

This document outlines the testing strategies, quality metrics, and verification steps used to validate PDFEngine rendering outputs.

---

## 1. Quality & Correctness Metrics

To measure rendering improvements objectively, we track metrics that reflect layout and pagination quality:

*   **Page Utilization Rate**: The percentage of the printable area actually occupied by content. We flag pages utilizing <40% height as having high whitespace risk.
*   **Orphaned Headings count**: The number of heading elements (`h1`, `h2`, `h3`) placed at the bottom of a page without at least 2 paragraphs following on the same page.
*   **Clipped Elements count**: The number of block containers marked with `overflow: hidden` where contents exceed the client width/height.
*   **Font Substitution / Missing Glyph count**: The count of characters falling back to generic serif/sans-serif fonts or rendering as blank boxes (tofu).
*   **Success Rate Metrics**:
    *   *OCR Extraction Success*: Percentage of searchable text matching the raw source inputs.
    *   *Barcode/QR Decode Rate*: Percentage of document barcodes correctly scanned and parsed.

---

## 2. Programmatic Verification Suite

Our testing pipeline evaluates the following verification steps:

```
[PDF Output] ➔ [Verification Runner]
                     ├── OCR read checks
                     ├── Bounding box overflow parses
                     ├── Bookmark trees outlines audits
                     └── Font embed binary audits
```

*   **PDF Binary Verification**: Audits output byte strings to verify metadata, PDF version 1.7 compliance, and embedded fonts.
*   **OCR Parsing Verification**: Extracts text layout nodes using PDF parsers to match the text structure against input source variables.
*   **Barcode Verification**: Runs image scans on output PDFs to verify barcode legibility and correct encoding values.

---

## 3. Performance & Capacity Gates

We enforce resource limits under concurrent testing:

*   **P95 Generation Duration**: Must remain under **500ms** for 10-page documents.
*   **P99 Generation Duration**: Must remain under **3000ms** for 1000-page stress files.
*   **Peak RAM usage**: playwrigth worker processes must not exceed **256MB** memory usage under continuous rendering loops.
