# Bundled font licences

Every font file in this directory is redistributable. This file records which licence
covers which family, because "we shipped a font we had no right to ship" is the same class
of problem as RB-6 (the ICC profile), and it is cheaper to record it now than to audit it
under a customer's legal review later.

## SIL Open Font License 1.1 — full text in [`OFL.txt`](./OFL.txt)

The OFL permits bundling, redistribution and embedding in documents. It requires that the
licence travel with the fonts (hence `OFL.txt`) and that Reserved Font Names are not reused
for modified versions — the engine ships these files unmodified.

| Family | Faces | Used for |
|---|---|---|
| **Carlito** | Regular, Bold, Italic, BoldItalic | The sans-serif face for engine-drawn text (footnote bands, running headers). Metric-compatible with Calibri |
| **Caladea** | Regular, Bold, Italic, BoldItalic | The serif face. Metric-compatible with Cambria |
| **Liberation Mono** | Regular, Bold, Italic, BoldItalic | The monospace face |
| Inter, Outfit, Montserrat, Space Grotesk, Playfair Display, Cinzel, Roboto Mono | Regular only | Web fonts available to documents |
| Noto Sans (Arabic, Bengali, Devanagari, Gujarati, Gurmukhi, Hebrew, JP, KR, Kannada, SC, TC, Tamil, Telugu, Thai, Vietnamese) | Regular only | Script coverage |
| Noto Color Emoji | Regular | Emoji coverage |

## Why the three four-face families are here

PDF text drawn by the engine — footnote bands and running headers — is drawn with
PdfSharpCore, not by the browser, so it can only use a face that is actually resolvable.
Before these were added every bundled file was a Regular weight and no font resolver was
registered at all, which meant **bold and italic silently rendered upright and regular, and
a requested `font-family` was ignored entirely**. Carlito, Caladea and Liberation Mono were
chosen because each ships a complete Regular/Bold/Italic/BoldItalic set under the OFL.
