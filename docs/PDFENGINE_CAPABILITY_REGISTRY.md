# PDFEngine — Verified Capability Registry

**Document ID:** PDFENGINE-REGISTRY-001
**Status:** Living record. This is the ONLY source a sales artifact may draw from.
**Last reconciled against branch:** `main`, 2026-08-16

---

## 0. How to read this file

A capability appears here **only** when someone has produced evidence against the
current branch. "It compiles", "the code exists", and "a previous report said so"
are explicitly *not* evidence.

| Status | Meaning |
|---|---|
| **VERIFIED** | Reproducible evidence exists against the current branch, recorded below. |
| **IMPLEMENTED** | Code exists and works in manual testing, but the certification gate (adversarial + regression + perf) is not satisfied. |
| **PARTIAL** | Works for a defined subset. The subset and the known limitation are both stated. |
| **BLOCKED BY UPSTREAM** | Limitation belongs to Chromium/Skia/PdfSharpCore. Cannot honestly be claimed as solved by us. |
| **MISSING** | Not implemented. |

**Evidence column rule:** must name the actual check performed. "Tested" is not
evidence. "veraPDF 144/144 rules, 0 failures, on Chromium-rendered output" is.

**Evidence-artifact rule (added 2026-08-16):** a row may only say VERIFIED, and a blocker
may only be marked CLOSED, when a committed file under [`tests/evidence/`](../tests/evidence/)
demonstrates it with a machine-readable verdict. See
[`tests/evidence/README.md`](../tests/evidence/README.md) for the standard and why it
exists — RB-3 was first closed on prose alone, which is the same failure mode as the
superseded `docs/` files.

---

## 1. Rendering core

| Capability | Status | Evidence |
|---|---|---|
| HTML5 / CSS3 / Flexbox / CSS Grid | VERIFIED | 23-page report renders Grid + Flex layouts correctly; grid-adjacent pagination bug found and fixed |
| SVG (paths, gradients, text, transforms) | VERIFIED | Line/area, bar, and donut charts computed from real data render correctly in output PDF |
| JavaScript execution (opt-in) | VERIFIED | Inline script with arrow fns/template literals/comparisons executes; result present in PDF text |
| Canvas / Chart.js class libraries | IMPLEMENTED | Works with `allowScripts` + wait strategy; no permanent regression fixture yet |
| `@page` size / margin / orientation | **VERIFIED (corrected 2026-08-18)** | **This row was previously an OVERCLAIM.** `@page { size: ... }` did NOT work: the CSS sanitizer stripped the `size` descriptor while allowing `margin`, so `@page{size:A5}`, `@page{size:A5 landscape}` and `@page{size:210mm 148mm}` all produced identical A4 output. Found by Gate A. Now measured working: A5→420x595, A5 landscape→595x420, 210x148mm→595x420, while margin-only and no-`@page` documents keep A4 unchanged. Evidence: [`compat-gate.log`](../tests/evidence/compat-gate.log) |
| Landscape / scale / page ranges | IMPLEMENTED | Wired to Playwright `PagePdfOptions`; no adversarial fixture |
| FullHeight (single continuous page) | VERIFIED | MediaBox height 540pt vs standard A4 842pt on `example.com` render |
| Print backgrounds | VERIFIED | Backgrounds present in rendered output |
| **Engine version pinning** | **VERIFIED** | Every response carries `X-PdfEngine-Engine-Version` (`2026.08.1+chromium145.0.7632.6`). `pinEngineVersion` REFUSES a mismatched render (HTTP 500 with an explicit message) instead of silently using a different Chromium — measured both directions. Evidence: [`determinism-gate.json`](../tests/evidence/determinism-gate.json) |
| **Deterministic clock / randomness / locale** | **VERIFIED** | `fixedDateUtc` freezes `Date.now()`, `new Date()` and `performance.now()`; `randomSeed` seeds `Math.random`; `timezone`/`locale` are applied via a dedicated browser context. Measured: an unpinned clock/random document produced 3 distinct outputs in 3 runs, pinned produced 1. Gate J `clock-and-random` fixture cannot pass without these |
| **Typography weight fidelity** | **VERIFIED (defect fixed 2026-08-18)** | Previously every `@font-face` declared one Regular file as `font-weight: 100 900`, so weights 100/400/900 rendered byte-identically — **bold was never bold**. Two bundled files were also the Thin weight mislabeled as Regular. Now: weight 700 measurably heavier (ink 0.00597 vs 0.00450). Found by Gate B1 |
| CSS gradient-text (`background-clip:text`) | BLOCKED BY UPSTREAM | Chromium PDF export does not clip gradient to glyphs; renders as solid block. Workaround: SVG text with gradient fill (verified working) |

## 2. Typography & internationalization

**Rule enforced here:** visual rendering and PDF text-layer correctness are tracked
as two separate gates. A script is never certified from visual appearance alone.

**Automated gate:** `tests/extraction_gate.py` (Release Gate B2). 15 fixtures, oracle =
poppler `pdftotext`, baseline committed at `tests/corpus/i18n/baseline.json`, evidence at
`tests/evidence/text-extraction-gate.json`. Self-tested: an injected regression is
correctly detected and exits non-zero.

**Measured result — 2026-08-16: `PASS=9, SPACING=4, PARTIAL=2, FAIL=0`.**

| Script | Visual | Text layer | Evidence |
|---|---|---|---|
| Latin (English) | VERIFIED | **PASS** | exact match |
| Cyrillic (Russian) | VERIFIED | **PASS** | exact match |
| Greek | VERIFIED | **PASS** | exact match |
| Bengali | VERIFIED | **PASS** | exact match |
| Tamil | VERIFIED | **PASS** | exact match |
| Thai | VERIFIED | **PASS** | exact match |
| Chinese Simplified | VERIFIED | **PASS** | exact match |
| Korean (Hangul) | VERIFIED | **PASS** | exact match |
| Arabic (no lam-alef) | VERIFIED | **PASS** | exact match — proves Arabic *shaping* is fine |
| Vietnamese / accented Latin | VERIFIED | **SPACING** | `Tiếng` → `Tiế ng` |
| Devanagari (Hindi) | VERIFIED | **SPACING** | `पीडीएफइंजन` → `पीडीएफइं जन`. Conjuncts (`समर्थन`, `संयुक्ताक्षरों`) extract **correctly** |
| Japanese (CJK) | VERIFIED | **SPACING** | `PDFEngineは` → `PDFEngine は` |
| Hebrew (RTL) | VERIFIED | **SPACING** | `PDFEngine תומך` → `PDFEngineתומך` |
| Arabic (with lam-alef) | VERIFIED | **PARTIAL** | `الاتجاه`, `المعاكس` do not survive — lam-alef ligature reverse-mapping |
| Mixed RTL+LTR one sentence | VERIFIED | **PARTIAL** | date token `2026-08-16.` lost in bidi context |
| Urdu, Persian | VERIFIED | **PASS** | Fixtures added; both extract every token (2026-08-18) |
| Telugu, Kannada, Gujarati, Punjabi | MISSING | MISSING | No fixtures yet |
| Automatic font fallback + missing-glyph reporting | MISSING | — | No coverage analysis; silent tofu possible |
| **Accessibility pre-flight diagnostics** | **VERIFIED** | Fires when `generateTaggedPdf=true`: (a) N lists with visible markers → will fail PDF/UA 7.1, with the verified `list-style:none` workaround; (b) N images without `alt` → will fail PDF/UA 7.3. Both confirmed live in `X-Render-Diagnostics`. Evidence: [`rb4-pdfua.log`](../tests/evidence/rb4-pdfua.log) |

**Key finding — this corrects both my earlier claim and the external review.** The
dominant defect class is **word-boundary spacing at script-transition points**, not
broken `ToUnicode`. Every character maps back correctly in the SPACING cases; only the
inferred word gap differs. That degrades exact phrase search but **not** copy/paste
fidelity or screen-reader character mapping. Only Arabic-with-lam-alef and bidi-mixed
content lose actual content.

**Commercial consequence (permitted wording):**
- **Full text-layer support:** Latin, Cyrillic, Greek, Bengali, Tamil, Thai, Chinese, Korean.
- **Supported, with a documented phrase-search caveat:** Vietnamese, Hindi, Japanese, Hebrew.
- **Visual rendering only — must NOT be claimed for copy/paste, search or screen readers:**
  Arabic containing lam-alef, and mixed RTL+LTR sentences. Reproduced in **bare Chromium
  with zero PdfEngine code in the path** → BLOCKED BY UPSTREAM.

## 3. Pagination intelligence

| Capability | Status | Evidence |
|---|---|---|
| Native CSS break awareness (`page-break-*`, `break-*`) | VERIFIED | Bug found where native breaks desynced internal page tracking, producing blank pages document-wide; fixed and reproduced clean (4 pages → 2 on repro) |
| Orphan-heading avoidance | VERIFIED | Measures real gap + real line-height of following content, not a guessed constant |
| Grid/Flex-aware break suppression | VERIFIED | Headings inside multi-column rows excluded from forced breaks; fixed a bug that split grids and stranded blank pages (61 → 58 pages on real doc) |
| `break-inside: avoid` / keep-together | VERIFIED | Block never split across boundary in 23-page report |
| Repeating `<thead>` across pages | VERIFIED | Confirmed across 18 consecutive pages of a 550-row table |
| Whitespace diagnostics with attributed cause | VERIFIED | Reports px + % + reason (author break vs orphan-avoidance) per page |
| Blank-page detection | VERIFIED | 0 empty pages across final 23-page report |
| **Overflow / off-page content diagnostics** | **VERIFIED (added 2026-08-18)** | Gate A found the Rendering Doctor detected NEITHER. Now reports elements overflowing the printable width (with widest overhang in px) and absolutely/fixed-positioned elements entirely outside the page box. Both are content the reader silently loses. Evidence: [`compat-gate.log`](../tests/evidence/compat-gate.log) |
| **Dangling cross-reference reporting** | **VERIFIED (defect fixed 2026-08-18)** | A `data-pdfengine-pageref` pointing at a non-existent id produced zero page-ref requests, so the entire resolution pass was skipped and the entry shipped as a **silent blank gap**. Now renders `?` plus a diagnostic. Found by Gate G |
| Rowspan/colspan across page boundaries | **VERIFIED** | Gate E: 60 rowspan groups straddling breaks — 0 labels lost, 0 duplicated, 0 values lost; colspan sections intact; thead on all 10 table pages; totals reconcile; taller-than-page row survives; 25-col table keeps last column. Evidence: [`table-gate.log`](../tests/evidence/table-gate.log) |
| Widows/orphans (true line-level fragmentation) | PARTIAL | CSS `orphans/widows` are caller-controlled and honored by Chromium; real line-box measurement REPORTS blocks CSS cannot satisfy. No engine-level line-box control. Gate T1-6a/b/c |
| Pagination regression protection | **VERIFIED** | Gate C+D 7/7 — grid breaks, native-break desync, ToC refs, keep-together, orphan headings, attributed whitespace, blank pages. Evidence: [`pagination-gate.log`](../tests/evidence/pagination-gate.log) |
| Constraint solver / candidate scoring optimizer | MISSING | Currently single-pass heuristics, not weighted candidate evaluation |

## 4. Document navigation

| Capability | Status | Evidence |
|---|---|---|
| PDF bookmarks / outline tree | VERIFIED | 13 nested entries, correct hierarchy and page targets in 23-page report |
| Page-number cross-references (auto-TOC) | **VERIFIED** (ligature bug found+fixed by Gate C; NFKC normalisation now applied both sides — see [`gate-cd-ligature-bug.log`](../tests/evidence/gate-cd-ligature-bug.log)) | 7/7 correct, **independently cross-checked against Chromium's native outline**. Resolved by two-pass render (render → read real PDF → substitute → re-render) after two DOM-geometry approaches were proven wrong |
| Internal link annotations (`<a href="#id">`) | VERIFIED | PDF destination annotation, kind=4, correct target page |
| External link annotations (`<a href="https://">`) | VERIFIED | PDF URI annotation with preserved URL |
| Standard CSS `target-counter()` syntax | **VERIFIED** | `content: target-counter(attr(href), page)` translated onto the existing resolver, so it inherits the read-the-real-PDF correctness. Gate `typesetting_gate.py` T1-2: 4/4 chapters resolve to their true pages |
| `string-set` + running headers/footers | **VERIFIED** | `@page { @top-center { content: string(chapter) } }`. Chromium's `headerTemplate` is one fixed template for the whole document, so this is engine work, not a pass-through. Pages resolved from the REAL PDF. Proven by differential render (header adds text vs the same document without it) and by carry-forward onto continuation pages that contain no heading. `counter(page)/counter(pages)` correct on every page |
| Leaders (`content: leader('.')`) | **VERIFIED** | Dot leaders render in a ToC; gate asserts >= 4 runs of 4+ dots |
| `@page :first` / `:left` / `:right` | **VERIFIED** | Chromium implements these; they were broken only by the sanitizer stripping `@page` descriptors. Cover margin and mirrored binding margins both measured |
| **Named pages** (`@page cover { }` + `page: cover`) | **VERIFIED (scoped)** | Chromium silently ignores `page: <name>` — re-measured 2026-08-18, a cover declared `size: A4 landscape; margin: 50mm` produced identical portrait geometry and identical margins to the body. Not fixable by stamping, because page geometry changes layout. Implemented as per-run re-render + stitch: consecutive top-level content sharing a page name is rendered on its own paper and the parts merged (verified first that a PdfSharpCore merge preserves per-page geometry). Everything document-wide is resolved against the STITCHED document — page counters, cross-references, footnote placement and the bookmark outline. Gate T1-7a–g: landscape cover + portrait body + A5 appendix in one file, per-run margins, continuous `Page X of Y`, correct cross-references, footnotes on their own differently-shaped page, undefined page name reported, and a strict one-pass no-op without named pages. **Scope:** page names bind at top-level-block granularity (a `page:` rule matching something nested resolves up to the block that owns the page); one extra render per run. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |
| **Footnotes** (`float: footnote`, `::footnote-call`, `::footnote-marker`) | **VERIFIED (scoped)** | Chromium supports none of it — measured: `float: footnote` content renders INLINE where authored (16% down page 1), and neither pseudo-element numbers anything. The engine lifts the content out of the flow, leaves a numbered call marker, resolves the call's page from the REAL rendered PDF, reserves bottom space, re-renders until every page holding a call has room, and draws the band. Gate `typesetting_gate.py` T1-5a–f: note at 92% down the page (vs ~15% inline), note on its call's page, no band/body overlap, roman numbering, oversize note reported, and a strict no-op for documents without footnotes. **Scope:** the reservation is uniform across the document by default; `the strategy is **chosen automatically per document** (`footnoteReservationMode: "auto"`, the default): the engine measures what uniform costs against what per-page costs and takes per-page when it reclaims roughly a quarter-page or more within its render budget, reporting the decision and its value every time. Gates T1-9a-f — a footnote-free page went from a 143pt bottom gap to 67pt, an uneven document is given per-page and an even one is not, and an explicit mode overrules the choice. `@page :nth(N)`, which would make per-page reservation native and free, is ignored by Chromium (measured). Bottom edge only — content cannot be pushed upwards, so `float: top` is always uniform. The band is drawn as plain PDF text, so links inside a footnote survive as real clickable annotations and the text arrives with punctuation exactly as authored (gate T1-5g), but **bold and italic now draw in real faces** (gate T1-5h asserts the finished PDF embeds `Carlito,Bold` and `Carlito,Italic`). That needed an `IFontResolver` — PdfSharpCore had none registered, so every family and every style resolved to one identical face, which is also why `font-family` in a margin box had never had any effect — plus three complete Regular/Bold/Italic/BoldItalic families under the SIL OFL (Carlito, Caladea, Liberation Mono; licences in `Fonts/FONT-LICENSES.md`). A family bundled Regular-only still cannot show emphasis and says so; per-page renumbering (`counter-reset: footnote` on `@page`) is detected and reported as unsupported. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |
| **Page floats** (`float: top` / `float: bottom`) | **VERIFIED (scoped)** | Chromium implements neither — measured, both edges rendered at the identical position 38% down page 1, indistinguishable from no float. Built on the same lift-resolve-reserve-redraw machinery as footnotes, sharing one reflow loop with them. Gate T1-8a–e: top float at 7% down with body text below it, bottom float at 85% with body text above, a bottom float and a footnote stacking on one page without overlap, rasterization reported, and `float: left`/`right` left completely untouched. **Scope:** a page float is arbitrary content, so it is CAPTURED AS AN IMAGE (2x, ~192 DPI) rather than redrawn. Its words are re-drawn over the image as an INVISIBLE text layer at the same coordinates (the technique a scanned document uses for OCR), so a floated table stays selectable, searchable and screen-reader readable — gate T1-8f. Verified: transparent-brush text round-trips through extraction intact. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |

## 5. PDF output features

| Capability | Status | Evidence |
|---|---|---|
| `/Info` metadata (Title/Author/Subject/Keywords) | VERIFIED | Present and correct in output |
| Metadata + outline under encryption | **VERIFIED** | Previously corrupted/skipped. Fixed by moving encryption to a post-pass: `TITLE='Encrypted Doc Title'`, outline `[Chapter One, Section A]` both intact under AES-256. Evidence: [`rb1-aes256-closed.log`](../tests/evidence/rb1-aes256-closed.log) |
| Encryption + permissions | **VERIFIED** | **AES-256 (`Standard V5 R6`)**, 7 permission flags verified individually via live API. `AllowAccessibilityExtract` unsupported by the AES backend and explicitly diagnosed. Evidence: [`rb1-aes256-closed.log`](../tests/evidence/rb1-aes256-closed.log) |
| Text watermark | VERIFIED | Present on every page |
| Image watermark w/ opacity | VERIFIED | Correct alpha after premultiplied-alpha bug fixed |
| PDF merge | VERIFIED | 2 source PDFs → correctly ordered 2-page output; `<2 files` rejected with 400 |
| Tagged PDF + XMP/`pdfuaid` identification | VERIFIED | Real `/StructTreeRoot`: `/H1`, `/P`, `/Table` with `/TH` `/Scope` + `/TD` `/Headers`, `/Figure` with `/Alt`, `/L`+`/LI`, `/Lang`. Survives PdfSharpCore post-process intact |
| PDF/A-2b and PDF/A-3b | VERIFIED | **veraPDF — 2b: 144/0 · 3b: 146/0**, re-validated after BOTH the RB-6 ICC swap and the PDF/UA XMP change (which briefly broke it: 142/2, caught and fixed). Evidence: [`verapdf/`](../tests/evidence/verapdf/). Encryption+PDF/A correctly rejected at validation |
| PDF/UA-1 conformance | **VERIFIED (scoped)** | veraPDF ua1 **106/0 `isCompliant=true`**, tagged with and without PDF/A, controlled same-HTML test. Excludes: Arabic content (RB-2), documents with visible list markers or alt-less images (both now diagnosed at render time). Not in CI. Evidence: [`rb4-pdfua.log`](../tests/evidence/rb4-pdfua.log) **Re-verified 2026-08-19** after the font-resolver and print-viewport changes: veraPDF 1.30.2, 6/6 fixtures conformant (PDF/A-2b 144/0, PDF/A-3b 146/0, PDF/UA-1 106/0). **Newly measured limitation:** tagged output does NOT compose with engine-drawn content — a tagged document plus a running header fails 4 UA checks and plus a footnote fails 11, because that content is drawn after the structure tree is built and is therefore untagged. Reported at render time; tracked as T3-1. |
| Image optimization (real re-encode) | VERIFIED | 273 KB → 27 KB (90%) WebP; skips when not a net win |
| AcroForms / form fields | MISSING | PdfSharpCore has no write-side field API — verified: `PdfTextField` has zero public constructors |
| Attachments / embedded files | MISSING | Prerequisite for PDF/A-3 e-invoicing |
| Split / rotate / flatten / N-up | MISSING | — |
| Linearization (fast web view) | MISSING | — |
| Digital signatures / PAdES | MISSING | See roadmap §5 for library decision (PDFsharp 6.2+ has free native CMS signing; PdfSharpCore does not) |
| CMYK / ICC / bleed / crop marks / PDF/X | MISSING | Print-production vertical |

## 6. Security

| Capability | Status | Evidence |
|---|---|---|
| Real HTML sanitization | VERIFIED | Ganss.Xss DOM-based. `<script>`, `onerror`, `javascript:` payloads injected via `templateData` do not survive into PDF **bytes** |
| Script-body integrity under sanitization | VERIFIED | Found sanitizer HTML-entity-encoded `<script>` contents (`=>` → `=&gt;`), silently breaking all modern JS; fixed and verified |
| SSRF defense (DNS-pinned fetch) | VERIFIED | `169.254.169.254` metadata endpoint blocked; single attempt, no retry storm, HTTP 400 `BLOCKED_URL` |
| SSRF coverage: loopback/private/link-local/CGNAT/multicast/IPv6-mapped | IMPLEMENTED | Implemented in `IsIpSafe`; only the metadata endpoint case has an executed test |
| Redirect re-validation | IMPLEMENTED | Max 5 hops, re-validated each hop; no adversarial test |
| Dev credential gating | VERIFIED | Seeded admin/test key wrapped in `IsDevelopment()` |
| Rate limiting | IMPLEMENTED | JWT bypass removed; no bypass-attempt test suite |
| DOM depth / node limits | **VERIFIED** | Nesting depth capped at 512 and rejected at validation. Closed a remote DoS: ~6,000 nested elements overflowed AngleSharp's parser stack and terminated the API process. Gate I + 7 unit tests |
| Fuzzing (HTML/CSS/SSRF), ZIP/decompression bombs | MISSING | — |
| **Tenant isolation** | **VERIFIED** | Two separately seeded tenants. B→A job read/download both 404; A→A own job 200. **A real leak was found here and fixed.** Evidence: [`rb5-tenant-isolation-vuln.log`](../tests/evidence/rb5-tenant-isolation-vuln.log) |

## 7. Platform / SaaS

| Capability | Status | Evidence |
|---|---|---|
| Async job queue + worker + webhooks | IMPLEMENTED | Redis queue, `PdfRenderWorker`, webhook delivery present; no integration test |
| Batch submission | VERIFIED | 50 items accepted (202), 51 rejected (400); per-item validation errors correct |
| Async-path validation parity | VERIFIED | Found async path had **no validator at all**; added `SubmitPdfJobCommandValidator` |
| HTML templating (Scriban) | VERIFIED | `{{ }}` substitution; malicious template data still sanitized |
| API auth rejection + validation surface | **VERIFIED** | 17/17 platform-gate checks vs the real stack. Evidence: [`rb5-platform-gate.log`](../tests/evidence/rb5-platform-gate.log) |
| Login/2FA/refresh, billing, quotas, webhooks, key rotation, tenant isolation | **REPORTED — NOT VERIFIED** | Controllers exist; **no automated coverage**. Named explicitly as the remaining RB-5 scope |
| Per-stage render diagnostics | VERIFIED | Sanitize/font/paginate/chart/encrypt/compile timings + warnings returned in `X-Render-Diagnostics` |
| Visual drift (pixel diff) | IMPLEMENTED | `ComputeVisualDrift` via SkiaSharp + unit tests; not exposed as a product API |
| Run-to-run determinism | **VERIFIED** | Gate J: 4/4 fixtures byte-length + structural + visual stable over 3 renders each, incl. remote webfont. Drift detection self-tested. Evidence: [`gate-j-determinism.log`](../tests/evidence/gate-j-determinism.log) |
| Customer-facing engine version pinning | **VERIFIED** | `X-PdfEngine-Engine-Version` returned on every render; `pinEngineVersion` refuses a mismatch. Deterministic clock/random/timezone/locale shipped |
| Cross-machine reproducibility | MISSING | Gate J detects drift on ONE machine; Chromium/font versions are not yet pinned in a container, and there is no `engine: "2026.09"` API option |

## 8. Test & verification foundation

| Asset | Status | Evidence |
|---|---|---|
| Unit tests | VERIFIED | **58 passing**, 13 test files. Evidence: [`unit-tests.log`](../tests/evidence/unit-tests.log) |
| Integration tests (API) | **PARTIAL** | `tests/platform_gate.py` 13/13 vs real stack; auth/billing/tenancy flows still uncovered. Evidence: [`platform-gate.log`](../tests/evidence/platform-gate.log) |
| E2E tests (Playwright) | MISSING | — |
| Load / sustained-render tests | **PARTIAL** | Gate K (`performance_gate.py`): scaling 1/5/20/100 pages, 10,000-row table (219 pages / 3.2s), 8-way concurrency, sustained-run leak proxy. Runs nightly. **Not** the full 10,000-render soak, cold-start, or 500/1000-page documents |
| Chaos / failure injection | **PARTIAL** | Gate L (`reliability_gate.py`) 7/7: browser-process kill + recovery, DNS failure, unreachable/slow assets, broken webfonts, malformed HTML, failure non-contagion. **Not** injected: Redis/DB/storage outage, full disk, network partition |
| Text-extraction diff gate | **VERIFIED** | 15 scripts, poppler oracle, committed baseline, regression self-tested. Evidence: [`text-extraction-gate.log`](../tests/evidence/text-extraction-gate.log) |
| veraPDF in CI | **VERIFIED** | `tests/conformance_gate.py` + `.github/workflows/release-gates.yml`. 6/6 conformant, fails the build on regression. Evidence: [`conformance-gate.log`](../tests/evidence/conformance-gate.log) |
| PAC (PDF/UA) validation | MISSING | Blocks any accessibility claim |
| Permanent fixture corpus | MISSING | Target 100+ document classes |

---

## 9. Open release blockers

| ID | Issue | Impact |
|---|---|---|
| ~~RB-1~~ | ~~Encryption is RC4 128-bit~~ | **CLOSED 2026-08-17.** Live output is now `Standard V5 R6 256-bit AES`. Implemented as a final PDFsharp 6.2 pass rather than a full migration, so metadata/outline/watermark/PDF-A/merge stay on already-verified PdfSharpCore. **Also removed two workarounds** — metadata and outline now survive encryption. Known gap: `AllowAccessibilityExtract` unsupported (7 flags vs 8), now explicitly diagnosed. Evidence: [`rb1-aes256-closed.log`](../tests/evidence/rb1-aes256-closed.log), [`rb1-feasibility.log`](../tests/evidence/rb1-feasibility.log) |
| ~~RB-2~~ | ~~Arabic text layer~~ | **CLOSED 2026-08-18.** Extraction gate **FAIL=1, PARTIAL=3, PASS=11 -> FAIL=0, PARTIAL=1, PASS=14**, no regressions; `arabic-ligature-heavy` **FAIL -> PASS** with exact codepoint match (`0627 0644 0627 062A 062C 0627 0647`). **Fix:** `ApplyActualTextToReversedRuns` attaches a logical-order `/ActualText` to Chromium's `/ReversedChars` runs. **Root cause (measured, after two wrong hypotheses):** the `/ToUnicode` CMap was already correct — `<00DA> <06440627>` expands lam-alef to lam+alef — but extractors reverse a visual-order RTL run at CHARACTER level, splitting any glyph whose value is multiple characters. The rewrite reverses GLYPHS instead. Scope is deliberately narrow: runs where every glyph maps 1:1 are untouched, because intervening there regressed `arabic-simple` from PASS to PARTIAL. `/ReversedChars` is REPLACED not nested — nesting double-reversed correct text. Locked by 5 unit tests (`ActualTextRtlTests`). Evidence: [`rb2-arabic-root-cause.log`](../tests/evidence/rb2-arabic-root-cause.log) |
| ~~RB-3~~ | ~~No text-extraction CI gate~~ | **CLOSED 2026-08-16.** Evidence: [`text-extraction-gate.log`](../tests/evidence/text-extraction-gate.log) (exit 0, "Gate PASSED"), [`text-extraction-gate.json`](../tests/evidence/text-extraction-gate.json). Runner `tests/extraction_gate.py`, baseline committed, regression detection self-tested |
| ~~RB-4~~ | ~~PDF/UA not validated~~ | **CLOSED 2026-08-17.** veraPDF PDF/UA-1 **`isCompliant=true` 106/0** for tagged output (with and without PDF/A). Fixes: XMP for tagged docs, `pdfuaid` schema, PDF/UA extension schema. Root-caused the last failure to Chromium's untagged list markers — no appearance-preserving fix exists, so the engine now **reports** it plus missing `alt` text. Evidence: [`rb4-pdfua.log`](../tests/evidence/rb4-pdfua.log), [`verapdf/ua1-*.xml`](../tests/evidence/verapdf/). **Claim wording:** "PDF/UA-1 validated by veraPDF for tagged output" — not blanket "PDF/UA compliant"; excludes Arabic (RB-2) and is not yet in CI |
| ~~RB-5~~ | ~~Platform layer has zero automated tests~~ | **CLOSED 2026-08-17.** `tests/platform_gate.py` — **17/17 pass**, baseline committed, regression self-tested. **Found and fixed a live P0 cross-tenant data leak** in the process (5 endpoints; job id alone read another tenant's job + PDF). Evidence: [`rb5-tenant-isolation-vuln.log`](../tests/evidence/rb5-tenant-isolation-vuln.log), [`rb5-platform-gate.log`](../tests/evidence/rb5-platform-gate.log), [`platform-gate.log`](../tests/evidence/platform-gate.log). **Remaining (tracked, not blocking):** login/2FA/refresh, billing/quota accuracy, webhook delivery, key rotation |
| ~~RB-6~~ | ~~ICC profile redistribution licence~~ | **CLOSED 2026-08-16.** Replaced macOS ColorSync file with a profile **generated** by Little CMS 2.17 (MIT) from IEC 61966-2-1 constants — nothing vendor-owned is redistributed. Evidence: [`rb6-icc-provenance.log`](../tests/evidence/rb6-icc-provenance.log), [`verapdf/`](../tests/evidence/verapdf/) — 4 documents re-validated **144/0 (2b)** and **146/0 (3b)** after the swap. Provenance: `src/PdfEngine.Infrastructure/Resources/sRGB.icc.PROVENANCE.md` |

---

## 10. Corrections applied to earlier claims

Recorded so the same overclaims do not recur.

1. **"Merge / PDF/A / tagged output are genuine differentiators"** — **WRONG.** Gotenberg (free, self-hosted) ships all three. They are table stakes, not a moat.
2. **"Arabic and Hindi supported"** — **WRONG as stated.** Visual ≠ text layer. Arabic text layer is broken upstream; Devanagari is largely fine but was misdiagnosed as broken due to a faulty extraction oracle.
3. **"Bookmarks need implementation"** (external review) — **WRONG.** Bookmarks, internal links and external links are all verified working.
4. **"Devanagari ToUnicode is broken"** (external review) — **WRONG.** poppler extracts conjuncts correctly; the reported garbling was a PyMuPDF artifact.
5. **Page-utilization ≈94.76%** — valid **for that corpus only**. Not a universal guarantee.
