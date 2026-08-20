# PDFEngine — Feature Backlog (Tiered Implementation Tracker)

**Document ID:** PDFENGINE-BACKLOG-001
**Status:** Living tracker. This is the single list of what is left to build.
**Created:** 2026-08-18 · **Last updated:** 2026-08-18
**Companion docs:** [`PDFENGINE_TARGET_ARTIFACT.md`](./PDFENGINE_TARGET_ARTIFACT.md) (the goal) ·
[`PDFENGINE_CAPABILITY_REGISTRY.md`](./PDFENGINE_CAPABILITY_REGISTRY.md) (what is proven) ·
[`PDFENGINE_RELEASE_GATES.md`](./PDFENGINE_RELEASE_GATES.md) (how it is proven)

---

## 0. Rules for this file

1. An item moves to **DONE** only when a gate or unit test proves it. "Implemented" is not done.
2. Every DONE item names its evidence file.
3. Feasibility claims must be **measured**, not assumed. T1-4 and T1-6 were re-classified
   on first measurement, T1-5's designed approach was measured wrong twice during its own
   build, and T1-7 was measured feasible (a PdfSharpCore merge preserves per-page geometry)
   *before* the stitch was built on it — that is the expected outcome of measuring, and the
   reason this column exists.
4. Nothing is deleted from this file. Items that turn out to be impossible are marked
   **BLOCKED BY UPSTREAM** with the evidence, so the question is not reopened later.

**Status vocabulary:** `TODO` · `IN PROGRESS` · `DONE` (with evidence) ·
`BLOCKED BY UPSTREAM` · `DEFERRED` (possible, deliberately not now).

---

## Progress

| Tier | Done | Total |
|---|---|---|
| Tier 1 — page-level typesetting | **9** | 9 |
| Tier 2 — PDF output features | **6** | 6 |
| Tier 3 — verification depth | 0 | 8 |

Gate: `tests/typesetting_gate.py` (42/42) · 31 parser unit tests · 8 footnote-layout unit tests · baseline committed.

**Tier 1 is complete.** Every item Chromium does not implement — running headers, standard
`target-counter`, leaders, footnotes, named pages, page floats, per-page reservation — is
engine work with a gate behind it.

---

## Tier 1 — Page-level typesetting

**Why this tier exists.** This is the difference between "HTML to PDF" and a document
typesetting engine, and it is the set of features Prince XML and DocRaptor have that we do
not. A customer cannot migrate off DocRaptor without these, regardless of how good the
rest of the engine is.

| ID | Feature | Status | Effort | Notes / evidence |
|---|---|---|---|---|
| **T1-1** | **Running headers/footers from content** — `string-set: name content()` + `@page { @top-center { content: string(name) } }` | **DONE** | M | The most-requested feature in this class; every real report needs the current chapter in the header. Chromium supports **none** of it — its `headerTemplate` is one fixed template for the whole document. Built on the existing per-page post-process stamping (same mechanism as watermarks) plus the two-pass planner's page mapping. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |
| **T1-2** | **Standard `target-counter()` syntax** — `content: target-counter(attr(href), page)` | **DONE** | M | We already resolve real page numbers, but only via the proprietary `data-pdfengine-pageref`. Standard syntax **is** the Prince/DocRaptor migration path — without it a prospect must rewrite every template to evaluate us. Implemented as a translation layer onto the existing resolver, so it inherits the "read the real PDF" correctness. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |
| **T1-3** | **Leaders** — `content: leader('.')` | **DONE** | S | Dot leaders in a table of contents. Small, but it is the single most visible sign of a typeset document rather than a printed web page. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |
| **T1-4** | **`@page :first` / `:left` / `:right`** | **DONE** | XS | **Re-classified on measurement.** Estimated as build work; measurement showed Chromium **already supports all three** — they had been broken only by the CSS sanitizer stripping `@page` descriptors (fixed 2026-08-18). Cover pages and mirrored binding margins work today. Needed a gate, not an implementation. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) |
| **T1-5** | **Footnotes** — `float: footnote`, `::footnote-call/-marker` | **DONE** | **L** | Legal, academic and financial documents. **Measured 2026-08-18:** Chromium supports NONE of it — `float: footnote` content renders INLINE exactly where authored (test marker appeared 16% into page 1, not at the page bottom), and `::footnote-call`/`::footnote-marker` produce no numbering. Built as designed: the planner lifts each footnote out of the flow and leaves a numbered call marker; the call's page is resolved from the REAL rendered PDF with the same fingerprint matcher as running headers; bottom space is reserved and the document re-rendered until every page holding a call has room; the band is then drawn in the same post-process pass that stamps margin boxes. **Two things the design got wrong and measurement corrected — both worth keeping.** (1) *Per-page* reservation, built first, is wrong: a forced break on page N shifts every later page, so every other page's anchor — taken from the same render — is stale the moment the first break lands. Verified: a three-footnote document came back with two pages holding a single paragraph each. Making it correct needs one render per footnote-bearing page, which is unusable on exactly the documents this feature is for. The reservation is therefore **uniform** (the bottom margin grows document-wide) and Chromium re-paginates in one pass; the cost is that pages with fewer footnotes lose the same band as the busiest page. (2) Growing Playwright's margin option does nothing when `preferCSSPageSize` is on and the document declares `@page { margin }` — verified: three renders at 93px/111px/129px produced byte-identical PDFs. The reservation is an injected `@page` rule instead, which wins the cascade either way. **Also found:** Pass 2's pre-render forced breaks go stale the moment the content area shrinks, which is what produced the stranded paragraphs above even after the per-page anchors were gone; they are now marked and discarded when a reservation is applied, with heading-orphan avoidance handed to Chromium's `break-after: avoid` (which measures against real page boundaries). Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) T1-5a–f |
| **T1-6** | **Widow/orphan control** | **DONE** | M | **Re-classified on measurement — the third time measuring changed this tier's plan.** Estimated as a fragmentation engine; measurement showed Chromium **implements orphans/widows correctly** (at 4/4 a paragraph that would have split 5/3 moved WHOLE to the next page). The real gaps were that the values were hard-coded to 2 with no caller control, and that the case CSS *cannot* satisfy — a block taller than a page — degraded silently. Now: `orphans`/`widows` options, plus real line-box measurement (Range client rects, merged per line) that REPORTS unsatisfiable blocks. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) T1-6a/b/c |
| **T1-7** | **Named pages** — `@page cover { }` + `page: cover` | **DONE** | **L** | **Re-classified on measurement, then built.** Chromium **silently ignores** `page: <name>` — re-verified 2026-08-18: a cover declared `size: A4 landscape; margin: 50mm` came out with the identical portrait geometry and identical margins as the body pages. It cannot be corrected in post-processing because page geometry changes *layout*, not just stamping. Built as the backlog predicted — per-section re-render and stitch: consecutive top-level blocks sharing a page name form one **run**, each run renders on its own paper (all other runs taken out of layout, geometry applied through an injected `@page` rule), and the parts are merged. **Feasibility measured before building:** a PdfSharpCore import-merge preserves per-page geometry — an A4-landscape, an A4-portrait and an A5 part came back as 842x595, 595x842 and 420x595 — without which this route would have silently flattened every named page. **The part that is easy to get wrong is everything document-wide**, so each is resolved against the STITCHED document, not a part: `counter(page)`/`counter(pages)`, `target-counter()` cross-references, footnote and page-float placement, and the bookmark outline (whose planner-estimated pages are re-derived from the merged PDF, since they are counted in one continuous DOM pass that stops being true the moment the document is rendered in parts). Cost is one render per run; a no-op for documents declaring none. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) T1-7a–g |
| **T1-8** | **Page floats** — `float: top`/`bottom` | **DONE** | L | Prince parity. **Measured 2026-08-18:** Chromium implements neither edge — a `float: top` box and a `float: bottom` box landed at the *identical* position, 38% down page 1, indistinguishable from no float at all. Built on T1-5's machinery exactly as predicted: lift out of the flow, resolve the page from the real rendered PDF by text fingerprint, reserve the band, iterate on measured free space, draw. The one genuine difference from footnotes is that a page float is **arbitrary content**, so it cannot be redrawn from text — it is captured as an image (at 2x, via `zoom`, so it prints near 192 DPI rather than 96) while the browser still has it laid out. **That cost is real and is reported**: a floated element containing text loses its text layer, and the render says so, naming the count. A floated photograph loses nothing. Both edges are driven by the SAME reflow loop as footnotes — they compete for the same page, and two loops would each undo the other's convergence; a page carrying both keeps footnotes lowest. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) T1-8a–e |
| **T1-9** | **Per-page footnote / page-float reservation** | **DONE** | **L** | T1-5 and T1-8 reserve the same band on every page, so a document with one crowded page loses that band throughout. This reclaims it, as an opt-in `footnoteReservationMode: "per-page"`; the default stays uniform. **Measured before building:** `@page :nth(N)` would make per-page reservation native and free, and Chromium ignores it — `:nth(2)` and `:nth(2n)` produced output identical to no rule at all, while `:first` correctly re-paginated the same document. So it is forced breaks after all, at **one render per page that needs a band** — the cost the original deferral named, confirmed rather than avoided. **Three bugs the build surfaced, all worth keeping.** (1) PDF reading order cannot be bucketed by a word's top or bottom edge: those track actual glyphs, so on one line "alpha" and "juliet" differ by several points and single lines scattered across buckets, producing anchor text that appears nowhere in the document. Group by BASELINE — the one value every word on a line shares. (2) Anchor text must be matched with all whitespace squashed and hidden content excluded: a call marker extracts as `golf1` but sits in the DOM as a separate `<sup>`, and a hidden footnote body splices itself into the middle of the very sentence being matched. (3) Found while testing this — the UNIFORM loop was creeping up by the current shortfall and still overlapped after four passes on a 3-page, 8-footnote document; it now jumps straight to `max(band)`, which is sufficient in one step because growing a margin by R gives every page R of free space. Measured result: a footnote-free page went from a 143pt bottom gap to 67pt, reclaiming 76pt of content area. Falls back to the uniform reservation — never to overlapping text — when a page cannot be cleared or the pass budget runs out, and says which. **Scope:** bottom edge only; content cannot be pushed upwards, so `float: top` keeps the uniform reservation in both modes. Evidence: [`typesetting-gate.log`](../tests/evidence/typesetting-gate.log) T1-9a–d |

---

## Tier 2 — PDF output features

**Why this tier exists.** These do not change how a document is laid out; they change what
the resulting PDF can be *used for*. Several map directly to revenue verticals.

| ID | Feature | Status | Effort | Notes |
|---|---|---|---|---|
| **T2-1** | **Attachments / embedded files** | **DONE** | M | Unlocks **Factur-X / ZUGFeRD e-invoicing**, a real EU revenue vertical. **Feasible on the current library after all** — the backlog assumed a library decision was needed; measurement showed PdfSharpCore exposes `PdfEmbeddedFile(document, bytes, checksum)` and a public catalog `Elements`, which is everything required. Each file is written as an `/EmbeddedFile` stream plus a `/Filespec`, registered BOTH in the catalog's `/Names/EmbeddedFiles` name tree (what the attachment pane lists) and in `/AF` (what makes it an *associated* file, required by PDF/A-3). **One bug worth keeping:** a MIME type contains a `/`, which a PDF name cannot, and hand-escaping it to `text#2Fxml` gets escaped again on save to `text#232Fxml` — veraPDF failed PDF/A-3 clause 6.8 test 1 on exactly that, which for an e-invoice is a rejected invoice rather than a cosmetic defect. PdfSharpCore escapes names itself. Verified with real tools, not by reading back our own writer: poppler `pdfdetach` lists and extracts it byte-identical, veraPDF confirms **PDF/A-3b compliant**. Attachments plus `PDF/A-2b` are refused with a 400 — PDF/A-2 permits only embedded PDF/A documents. Evidence: [`output-gate.log`](../tests/evidence/output-gate.log) T2-1a–f |
| **T2-2** | **Digital signatures / PAdES B-T** | **DONE** | L | Signed contracts and approvals. **Library decision taken and recorded:** PDFsharp 6.2.1 — already a dependency for AES-256 — is used for the signature, in the same contained way and for the same reason, NOT as a migration. `IDigitalSigner` has three members, so the CMS is produced by the engine with `System.Security.Cryptography.Pkcs`; no new dependency. **PDFsharp's own signature computation cannot be used, and this is the finding worth keeping:** it builds the structure correctly (`/Sig`, the form field, `/ByteRange`, a fixed-width `/Contents` placeholder) but signs the WRONG BYTES — measured, it hands the signer 2,133 bytes beginning with whitespace while writing a `/ByteRange` declaring 2,922 beginning at the PDF header, and openssl rejects the result. The engine therefore lets PDFsharp lay out the placeholder, then computes the detached CMS itself over exactly the two ranges the FINISHED file declares and patches it into the fixed-width slot — nothing shifts, so the bytes signed and the bytes declared are the same by construction. Verified independently: `openssl cms -verify` accepts it, and flipping one byte inside the signed range makes it fail. SHA-256, `adbe.pkcs7.detached`. **Raised to PAdES B-T:** supplying `timestampUrl` attaches an RFC 3161 timestamp as the `id-smime-aa-timeStampToken` unsigned attribute, verified against a free public authority (DigiCert). This is not cosmetic — without a trusted timestamp the only evidence of WHEN a document was signed is a clock the signer controls, and the signature stops being verifiable the day the certificate expires. Everything needed is in the framework (`Rfc3161TimestampRequest`, `SignerInfo.AddUnsignedAttribute`), so it costs no dependency. An unreachable authority FAILS the request rather than returning an untimestamped signature. **Scope:** the signature is INVISIBLE (a visible appearance needs a font resolver registered for PDFsharp too, which is a different one from the engine's); signing and encryption are refused together, because encrypting rewrites the bytes the signature seals; there is no ContactInfo option, because PDFsharp accepts one and never writes it. Also worth knowing: `EphemeralKeySet` is unsupported on macOS, so the key is loaded `Exportable` and disposed immediately. Evidence: [`output-gate.log`](../tests/evidence/output-gate.log) T2-2a–f |
| **T2-3** | **AcroForms / form fields** | **DONE** | L | **Previously marked BLOCKED BY UPSTREAM — that was the wrong conclusion from a true observation, and it is worth recording why.** `PdfTextField` really does have zero public constructors in PdfSharpCore 1.3.67 AND PDFsharp 6.2.1, and the row concluded the capability was unavailable. But a form field is not a library type: it is a widget annotation on a page plus an entry in the catalog's `/AcroForm`, and both are ordinary dictionaries this engine already writes by hand for attachments. What was missing was a convenience API, not the capability. Text fields and checkboxes are placed from top-left coordinates (converted to PDF's bottom-left origin), with `/DA`, base-14 Helvetica in `/DR`, print flag set, and read-only/required flags. Verified with an independent reader: PyMuPDF reports a form PDF with the right names, types, values and ordering. Fields plus PDF/A are refused — `NeedAppearances` asks the reader to draw the controls while archival conformance demands baked appearances and embedded fonts. **Lesson: 'the library has no API for it' is not the same as 'the format cannot do it', and only the second is a blocker.** Evidence: [`output-gate.log`](../tests/evidence/output-gate.log) T2-3a–e |
| **T2-4** | **Split / rotate / flatten / N-up** | **DONE** | S–M | Mechanical page operations, exposed as one `POST /api/v1/pdf/transform` endpoint (merge keeps its own, because it is the only one taking many files). `extract` preserves the ORDER written, so `3,1` reorders and `1,1` duplicates — sorting the selection would quietly refuse what was asked. `rotate` is additive and normalised mod 360, so a page that already carried a rotation is turned BY the amount asked for rather than reset TO it. `nup` places 2/4/6/8/9/16 pages per sheet via `XPdfForm`, fitted and centred rather than stretched, and flips the sheet's orientation for grids wider than they are tall — **text survives**, which an implementation that rasterised the pages would not. `flatten` removes the interactive layer (annotations, `/AcroForm`) and deliberately does NOT rasterise: text stays text, so a flattened document stays searchable. **One fixture bug worth keeping:** rotation was first asserted via the page's width and height, and a page at `/Rotate 180` still reports a landscape rect — the rect is a derived quantity and is not a proxy for rotation. Assert `/Rotate` itself. Evidence: [`output-gate.log`](../tests/evidence/output-gate.log) T2-4a–e |
| **T2-5** | **Linearization (fast web view)** | **DONE** | M | Byte-serves page 1 before the whole file arrives. Delegated to `qpdf` (Apache-2.0) rather than reimplemented: linearizing means rebuilding the xref, renumbering objects and emitting a correct hint stream, and a subtly wrong version produces a file readers accept and silently fail to stream. Verified by asking qpdf itself. Runs after every byte-changing pass. **Measured:** qpdf linearizes an AES-256 document with the password and preserves the encryption (R=6), so no restriction there — the password goes over STDIN, never the argument list. **Linearize + signing IS refused:** signing rebuilds the document through PDFsharp, which undoes the object ordering — measured, the result came back signed and NOT linearized with nothing to indicate it. A missing `qpdf` binary FAILS the request rather than silently returning an unlinearized file. Evidence: [`output-gate.log`](../tests/evidence/output-gate.log) T2-5a–c |
| **T2-6** | **Bleed / crop marks / page boxes** — *CMYK and PDF/X deliberately excluded* | **PARTIAL (by decision)** | L | **Split on what can be verified.** DONE: bleed, crop marks and the page boxes a printer actually cuts to. Bleed is applied at RENDER time — the page is rendered at trim plus bleed so backgrounds genuinely run past the cut line, rather than being centred on a larger sheet, which would satisfy the boxes and still leave a white sliver when trimmed. The finished size is then recorded as the `TrimBox`, the bleed as the `BleedBox`, and crop marks get their own margin outside both. **Two defects found by asking whether it was real rather than present.** (1) Landscape was ignored: setting an explicit Width/Height while `Landscape` was still true made Chromium swap them back, so every landscape job got a PORTRAIT trim box — A4 landscape came out 210x297 instead of 297x210. (2) The TrimBox inherited Chromium's rounding of the requested pixel size and sat up to 0.3mm off the ordered paper; it is now taken from the NOMINAL size and centred, exact to 0.01mm across A4/A5/A3/Letter in both orientations. `/ArtBox` is set too, because some workflows otherwise fall back to the MediaBox and place the artwork including the crop-mark margin. Crop marks are keyed to the trim rectangle and offset past the bleed so none prints inside the finished page. **NOT DONE, and not claimed: CMYK conversion and PDF/X conformance.** Chromium emits RGB and converting needs a real colour-management pipeline; the usual engine for it is Ghostscript, which is **AGPL** — a licensing decision of exactly the kind RB-6 existed to avoid — and no validator available here checks PDF/X, so conformance could not be proven even if it were attempted. An unverified conformance claim is worse than none. Every print job says so at render time. Evidence: [`output-gate.log`](../tests/evidence/output-gate.log) T2-6a–e |

---

## Tier 3 — Verification depth

**Why this tier exists.** Every gate today is real but bounded. These close the gap between
"our gates pass" and "we can defend this to a customer's security or accessibility review".

| ID | Item | Status | Effort | Notes |
|---|---|---|---|---|
| **T3-1** | **PDF/UA validation across COMBINATIONS** | DONE (PAC itself N/A) | M | **Blocks any accessibility claim we want to sell.** veraPDF `ua1` passing is necessary but not sufficient — PAC is what accessibility auditors actually run |
| **T3-2** | **Permanent fixture corpus (100+ document classes)** | TODO | L | Gates test features; a corpus tests *documents*. Target: invoices, reports, statements, certificates, contracts, catalogues, manuals |
| **T3-3** | **Fuzzing** — HTML/CSS parser, SSRF, ZIP/decompression bombs | DONE — found 2 real bugs | M | Gate I covers a fixed adversarial list. Fuzzing finds the case nobody wrote a test for — and the DoS found on 2026-08-18 shows that class of bug is present |
| **T3-4** | **Cross-machine reproducibility** | DONE | M | Gate J proves determinism on ONE machine. Pinning Chromium + font versions in a container is what makes the reproducibility claim portable |
| **T3-5** | **Full 10,000-render soak + cold-start** | DONE (re-run pending, see notes) | S | Gate K runs a bounded 200-render leak proxy. The soak is the Gate K exit condition |
| **T3-6** | **E2E tests (Playwright)** | TODO | M | API and gates are covered; the browser-facing flow is not |
| **T3-7** | **Chaos** — gate built and run; **6 real findings, all OPEN** | GATE DONE / FIXES OPEN | M | Gate L injects process-level faults only; infrastructure faults need container control |
| **T3-8** | **Remaining scripts** — Telugu, Kannada, Gujarati, Punjabi | TODO | S | Urdu and Persian were added 2026-08-18 and both PASS |

---

## Known upstream limitations (closed questions — do not re-open)

| Item | Evidence |
|---|---|
| CSS gradient-text (`background-clip: text`) | Chromium's PDF export does not clip the gradient to glyph shapes; it renders as a solid block. Verified workaround: SVG text with a gradient fill |
| Arabic/Hebrew extract in **visual** order with bidi controls | Correct behaviour, not a defect. `/ActualText` (RB-2) makes the logical text recoverable |
| Word-boundary spacing at script transitions | Every character maps back correctly; only inferred word gaps differ. Affects exact phrase search, not copy/paste or screen readers |

---

## Release blockers (RB-1 … RB-6) — all closed

| ID | Blocker | Closed |
|---|---|---|
| RB-1 | Encryption was RC4-128 only | AES-256 via PDFsharp `SetEncryptionToV5`, fail-closed |
| RB-2 | Arabic text layer unusable | `/ActualText` on RTL runs; extraction gate FAIL=1 → **0** |
| RB-3 | No text-extraction CI gate | `tests/extraction_gate.py`, 19 fixtures, baseline committed |
| RB-4 | PDF/UA never validated | veraPDF PDF/UA-1 **106/0 `isCompliant=true`** |
| RB-5 | Platform layer untested | `tests/platform_gate.py` 17/17; **found and fixed a live P0 cross-tenant leak** |
| RB-6 | ICC profile redistribution licence | Resolved |

No release blockers are currently open. **That is not the same as release-ready** — see the
gate scoreboard, where Gates I, K and L are PARTIAL by design.

---


### T3-7 chaos — all six findings closed, 16/16

`tests/chaos_gate.py` stops real dependency containers. All sixteen assertions now hold.
One earlier conclusion recorded here was WRONG and is corrected below.

| # | Fault | Was | Now |
|---|---|---|---|
| C-1 | Redis stopped | render **500** `INTERNAL_ERROR` | **503 + Retry-After** in 0.0s |
| C-2 | Postgres stopped | render **500** `Name or service not known` | **503 + Retry-After** in 0.1s |
| C-3 | MinIO stopped | recorded as "the process exits" — **that was wrong** | see below |
| C-4 | MinIO stopped | `/health` gave **no answer in 25s**; render took **68.9s** | `/health/live` **200 in 0.03s**, render **1.2s** |
| C-5 | Network partition | unmeasurable, process assumed down | **200 on loopback while fully partitioned** |
| C-6 | Network partition | render assumed hung | **503 in 15.2s**, a definite answer |

**Correction to C-3.** The first chaos run reported `RestartCount 1` and exit code 0, and
that was read as an S3 outage stopping the .NET host. Re-measured: the restart was a
`docker compose --force-recreate` from this session, not a crash. The process never died.
What actually happened is C-4 — the health endpoint HUNG, because the S3 check had no
timeout of its own. That is worse than it sounds and better than reported: nothing crashes
locally, but a Kubernetes liveness probe on that endpoint kills a perfectly healthy
container, so a ten-second bucket blip becomes a restart loop across every replica.

**What changed.**

- **Liveness and readiness are now separate endpoints.** `/health/live` consults no
  dependency and answers in 0.03s — point the orchestrator's liveness probe there.
  `/health/ready` reports the dependencies, each with its own 3s timeout, and returns 503
  when one is down — point the load balancer there. Being unready removes a replica from
  rotation; killing it does not help. The container HEALTHCHECK now uses `/health/live`.
- **Dependency failures are 503 with `Retry-After`, not 500.** A 500 is a bug report that
  gets a human out of bed; a 503 is a retry the client already knows how to do.
  `GlobalExceptionMiddleware` classifies Redis/Npgsql/S3/socket failures by walking the
  inner-exception chain, and logs them as errors rather than as critical — paging on a
  dependency outage trains people to ignore the page.
- **The S3 client got a short leash** (5s timeout, 1 retry, down from the SDK default of
  100s and 4 retries). That default is sized for a hiccup talking to a bucket that exists;
  against a bucket that is down it cost 68.9s on a render that normally takes 0.3s, holding
  a tenant render slot throughout.
- **Postgres connect timeout is 5s** (`Timeout=5`), so an unreachable database is discovered
  in five seconds instead of thirty.
- **`PdfRenderWorker`'s queue read is guarded** with exponential backoff, and
  `BackgroundServiceExceptionBehavior.Ignore` is set so no worker can stop the host. Neither
  turned out to be the cause of C-3, and both are correct regardless: the dequeue was
  covered only by `catch (OperationCanceledException)`, so a Redis connection failure would
  have escaped `ExecuteAsync` and, on .NET 6+ defaults, stopped the host.

**One assertion in the gate was wrong, not the engine.** "A synchronous render still
succeeds while Redis/Postgres are down" was wishful: authenticating an API key needs the
database and charging a quota needs Redis. The gate now asserts what the engine actually
owes — a retryable answer, never a 500 — and says so in its docstring.

### T3-5 soak — re-run clean, drift question answered

10,000 renders, concurrency 2, **0 errors**. Cold start: health at 2.3s, first PDF at 4.4s
(2.1s of that is the browser launch — an autoscaler's grace period must exceed it).

**The +70% latency drift was the host, not the engine.** Re-measured with the fuzzer and
the build loop off the machine: **+3.4%** (224ms first tenth to 231ms last tenth). Median
243ms, p95 692ms, p99 1377ms.

`max` for the run reads 1,073,711 ms — **17.9 minutes for one render**. That is also the
host: macOS hit load average 17.9 with heavy pageouts partway through and throughput fell
from 7.8/s to 0.7/s before recovering. The container was using 738 MB of a 3.8 GB limit
throughout. The gate now records the host's load average beside every sample and refuses to
report drift as a verdict when load exceeded 8 — twice a soak here has produced a number
that looked like a regression and lived in the load average.

**The memory trend was not real, and the heap profile found nothing because there was
nothing in the heap.**

Before profiling the .NET heap, the container's memory was broken down by process, which
is where the answer was:

| | At rest | Under load (concurrency 2) |
|---|---|---|
| .NET (`PdfEngine.API`) | ~390 MB | ~390 MB — **flat** |
| Playwright driver (node) | ~235 MB | ~230 MB |
| Chromium | 218 MB across **3** processes | 1091–1399 MB across **15–19** processes |

Sampling RSS mid-flight measures how many Chromium workers happen to be alive at that
instant. That is a function of concurrency, not of leakage, and the apparent
"+11.6 MB per 1000 renders" was that sawtooth being sampled at arbitrary points.

The measurement that settles it is the FLOOR:

| After | Memory at rest |
|---|---|
| 2,500 renders | 843 MB |
| 5,000 renders | 848 MB |

The first 2,500 renders added 43 MB and the next 2,500 added **5 MB** — bounded warm-up
(font caches, JIT, pooled buffers), not a leak, which would charge the same amount every
batch. **No leak. Nothing to fix in the engine.**

What was fixed is the gate, which was asking the wrong question. It now quiesces the load
and takes a median at-rest reading before and after the run, and the leak verdict comes
from that comparison; the in-flight samples are still printed, labelled as the shape of the
sawtooth rather than as evidence. The lesson worth keeping: **a leak moves the floor, so
measure the floor.**

### T3-1 — what was fixed, and PAC

Running headers, folios and watermarks are now emitted inside `/Artifact` marked-content
blocks, so they compose with tagged output instead of breaking it. Embedded CIDFontType2
fonts get the `/CIDToGIDMap` ISO 32000-1 Table 117 requires. Measured with veraPDF 1.30.2:

| Combination | Before | After |
|---|---|---|
| tagged alone | 1492 / 0 | 1492 / 0 |
| tagged + running header | 1553 / 2 | **1560 / 0** |
| tagged + watermark | 1523 / 2 | **1530 / 0** |
| tagged + header + watermark | not measured | **1598 / 0** |
| tagged + footnote | 1625 / 8 | **1647 / 0** |
| tagged + Chromium headerTemplate | 1571 / 3 | **1604 / 0** |
| tagged + page float | not measured | **1506 / 0**, carrying the author's description |

Footnotes are **not** artifact-marked, and that was the whole difficulty. Declaring the band
furniture would have turned 7 failed checks into 0 while hiding the footnote from the screen
reader it exists for. It is a real `/Note` structure element instead, which means keeping
four things consistent: the drawing wrapped in `/Note <</MCID n>> BDC … EMC` with an n unused
on that page, a `/StructElem` of subtype `/Note` pointing at the page and that MCID, that
element parented into the document hierarchy, and the page's ParentTree entry extended so
index n resolves back to it. Miss the fourth and the tree looks right in a dump while
assistive technology cannot walk from the mark to its element — worse than untagged, because
it reads as tagged.

Verified beyond veraPDF's verdict: the Note is a child of the document element, the
ParentTree slot for its MCID resolves back to the Note, the marked-content block opens and
closes in the page stream, the footnote text still extracts, and `/Artifact` appears nowhere
in it. The eight footnote typesetting cases (T1-5a…h) still pass, so the tagging did not
disturb the layout. **Chromium's `headerTemplate` was written off here as upstream and unfixable, and that was
wrong.** The observation was true — Chromium draws it untagged and offers no hook — but the
content stream belongs to the engine once Chromium hands the file over. In a tagged document
Chromium marks all real content, so a text object sitting at marked-content depth zero is by
construction the content it did not tag: the header, the footer and the page number, and
nothing else. Those are wrapped as `/Artifact` after the fact. Whole `BT…ET` text objects are
wrapped rather than arbitrary spans, so the inserted BDC/EMC cannot interleave with the
surrounding q/Q graphics-state pairs and corrupt the stream. 1571/3 → **1604/0**, with the
header still rendering and `qpdf --check` clean.

**Page floats are now `/Figure` elements** carrying the author's own `aria-label`, `alt`,
`<figcaption>` or `title`. A float is drawn as pixels, so that description is the ONLY thing
a screen reader gets — there is no text layer under it. A float with no description still
validates (a generic label is legal) and the engine reports it, because "Figure 1" tells a
reader who cannot see the figure nothing.

One bug worth keeping from that work: the first version produced a perfectly well-formed
`/Figure <</MCID 2>> BDC EMC` with the image drawn OUTSIDE it, because the XGraphics had been
created before the BDC and writes into the stream it appended at construction. veraPDF passes
an empty figure without complaint, which is exactly why it had to be checked by reading the
content stream instead of the verdict. The graphics context is now opened per float, inside
its own block.

**PAC cannot be automated.** PAC (axes4) is a Windows-only GUI with no command line, so it
runs on no machine this project builds on. veraPDF's PDF/UA-1 profile implements the same
Matterhorn Protocol machine checks and is what `tests/accessibility_gate.py` runs. PAC stays
a manual pre-release step on Windows.

## Release readiness — configuration and deployment

The rendering engine passed every gate long before the SERVICE around it was fit to deploy.
Five things stood between them, all configuration, none of them visible from any test that
renders a PDF. Closed 2026-08-20, each verified against the container:

| # | Was | Now |
|---|---|---|
| 1 | The JWT signing key was committed to the repository — anyone with the source could forge a token for any tenant, including admin | `StartupConfigValidator` refuses to start when a committed value reaches a non-Development environment. Verified: Production with the committed key exits naming `Jwt:Key`, `Stripe:SecretKey` and the connection string; Production with real secrets boots and serves |
| 2 | `EnableDetailedErrors: true` in base config, overridden nowhere — raw exception text to callers | `false` in base, `true` only in Development |
| 3 | CORS hardcoded to `localhost:3000/3001` — the production dashboard could not call the API | Configuration-driven, with the resolved origins logged at boot |
| 4 | Nothing applied migrations anywhere in `src/`. A fresh database had no schema and failed on the first request that touched a table | `--migrate-and-exit` for a deploy step, `Database:MigrateOnStartup` for single-instance. Default is neither |
| 5 | No Production config, no env matrix, compose shipping `minioadmin`/`pdfpassword` | `appsettings.Production.json` (no secrets, no hostnames), `docs/DEPLOYMENT.md`, compose reads credentials from the environment |

Also done: `mem_limit: 3g` sized from the measured ~2.0 GB peak (unlimited before — one
expensive document could evict everything on the node), `UseForwardedHeaders` so a proxied
deployment does not collapse every rate-limit bucket onto the proxy's address, and
`docker/alerts.yml` — five Prometheus rules, all verified as `health=ok`, built on the
503-vs-500 split the chaos work created.

**A length check cannot catch a committed secret.** The JWT key in this repository is 49
bytes of well-formed nonsense and satisfies every structural rule there is. The only thing
distinguishing it from a real secret is that everyone with the source has it, so the check
has to be equality against the known values, and it has to stop the process.

## Implementation notes worth keeping

**The render budget did not cover the render.** `MaxRenderDurationSeconds` was applied
inside the attempt loop, and the pagination planner runs BEFORE it. A 24 MB SVG found by the
fuzzer therefore ran for **342 seconds** holding a tenant render slot, while the 30s budget
meant to bound it sat unused a few lines below. Both the analysis stages and the PDF capture
are now inside the budget and a breach returns 408, not a hang. Cancellation is
checkpoint-based, so a breach still overshoots (measured 298s against a 180s Enterprise
budget) — bounded and honest, not tight.

**A fuzzer earns its place on the first run or not at all.** `tests/fuzz_gate.py` found two
real bugs in 300 inputs: the hang above, and `pageRanges` values like `9999999-1` reaching
Chromium and coming back as HTTP 500 — the caller's malformed input reported as the server's
fault. The regex validated the SHAPE and never looked at the numbers.


**A field with no appearance stream is an invisible field.** The form fields were correct
in every structural sense — `/FT`, `/DA`, `/Rect`, listed in `/AcroForm /Fields`, found by
two independent readers — and drew nothing at all on the page. `/NeedAppearances true` asks
the READER to generate appearances, and macOS Preview ignores it. Every gate passed, and
the first person to open the document found an empty box where a form should be. Widgets
now carry a real `/AP` (plus `/MK` border and background), `/DR` includes `/ZaDb` so a
reader that regenerates the checkbox can find the tick font, and gates `T2-3f`/`T2-3g`
assert visibility and an external fill round-trip rather than structure alone. The general
rule: **a gate that reads the object tree has not checked what a user sees.**

**External verification lives in `tests/verify_tier2.py`.** Fifteen Tier 2 claims checked by
poppler, openssl, qpdf, pypdf and PyMuPDF — never by this engine's own diagnostics. A tool
that is not installed reports UNCHECKED, not PASS and not FAIL.

**A tool that could not run is not a failing test.** `verapdf_compliant()` returned `False`
when veraPDF produced no report. veraPDF is a Java program, so on a host with no JVM the
PDF/A-3b gate reported the engine as NON-CONFORMANT when the real finding was UNCHECKED —
an hour of hunting a defect that was a missing JDK. It returns `None` now and the case
SKIPs. Any gate that shells out to an external verifier needs the same three-way result:
pass, fail, could-not-check.

**The proof sheet is the composition test.** Building one document that uses every Tier 1
and Tier 2 feature at once has now twice found defects that the per-feature gates missed,
because gates exercise features in isolation and customers do not. It is worth re-rendering
after any change to the render pipeline, not only when the document's content changes.
Source lives at `docs/proof/capability-proof-sheet.html`; it is rendered with attachments,
form fields and a B-LT signature all active, so the file demonstrates the claims it makes.

**Reserving vertical space is not the same as forcing a page break.** T1-5 was designed
around forcing a break before whatever content would be overrun, and that design is wrong
for a reason worth stating once: page N's break shifts pages N+1 onward, so every anchor
computed from the same render is stale as soon as the first one is applied. Measured
result was two pages holding a single paragraph each. Anything that needs to reserve space
across pages — T1-8 page floats next — should reserve through the page box (a `@page`
margin) and let Chromium re-paginate, and iterate on MEASURED free space rather than on
the reservation it asked for.

**`preferCSSPageSize` silently disables the print API's margins.** It defaults on, and a
document that declares `@page { margin }` — which is most documents that also want
footnotes — makes Chromium take margins from CSS and ignore `PagePdfOptions.Margin`
entirely. Three renders at 93px/111px/129px produced byte-identical PDFs before this was
found. Any future feature that needs to change page geometry at render time should inject
a `@page` rule, which wins the cascade in both configurations.

**Pass 2's forced breaks are estimates with an expiry.** They are computed against the
page height at planning time; anything that later changes the content area invalidates all
of them, and a stale forced break strands whatever follows it on a page of its own. They
are now marked `data-pdfengine-planner-break` so a later stage can discard them, and the
footnote reservation does exactly that — handing heading-orphan avoidance to Chromium's
`break-after: avoid`, which measures against the real page boundaries rather than an
estimate of them.

**A switch the caller cannot set correctly should not be a switch.** T1-9 shipped as an
opt-in mode, and the question that exposed the design was simply "which of my documents
need it?" — the caller cannot tell from their HTML, because it depends on how unevenly the
footnotes fall, which does not exist until the document has been paginated. The engine had
already measured exactly that by the end of its first pass. It now decides per document and
reports what it chose and what the alternative would have cost. Off by default loses pages
of content silently; on by default bills every document for renders it does not need.

**The engine measured in screen media at 1280px and printed in print media at 660px.**
Two mismatches, both silent, both making every measurement the planner takes systematically
wrong. The viewport was Playwright's default while an A4 page with 20mm/16mm margins is
~660px wide, so text wrapped differently when it was measured than when it was rendered and
a `width: 100%` page float measured nearly twice its printed size. Worse, the page was in
SCREEN media while `page.PdfAsync` renders in PRINT — so the `@media print` rules the engine
injects for its own use (orphans/widows, break-inside, repeating table headers) were
invisible to the pass that measures against them, as was every print stylesheet the author
wrote. Both are now set before the content loads, so fonts, images and charts settle at the
final width instead of reflowing underneath the measurement.

Two gate cases had been passing for the wrong reason and were rewritten rather than
re-baselined:

* **T1-6b** proved caller-controlled orphans/widows by observing a short paragraph move
  whole. That is the PLANNER's late-start rule, not the CSS, and it fired either way — the
  case never reached the setting it claimed to test. It now asserts the widow count
  directly: raising it must carry more of the paragraph onto the continuation page
  (measured, 2 lines becomes 5).
* **T1-9e** asserted an "uneven" document gets per-page reservation, using a document short
  enough that per-page reclaims only 0.2 of a page — which is a document where UNIFORM is
  the correct answer. The fixture now describes a document where the trade actually pays.

Cost, measured: normal documents are unchanged (a 100-page render is still 245ms, 8/8
performance gate). A pathological 3.7MB / 120,000-paragraph document went from comfortable
to ~285s against the security gate's 300s ceiling, because laying out that much content at
660px is genuinely more work than at 1280px. It still returns 200, never a 5xx.

**There was no font resolver at all, and nothing said so.** PdfSharpCore ships a default
that was measured to return ONE identical face for every family and every style —
"Helvetica", "Arial", "Times New Roman" and "Verdana" all measured byte-identical, and so
did Regular, Bold and Italic. Two features had therefore never worked and never complained:
`@page { @top-center { font-family } }` did nothing, and emphasis inside a footnote rendered
upright. An `IFontResolver` over the bundled fonts fixes both, and three complete
Regular/Bold/Italic/BoldItalic families under the SIL OFL (Carlito, Caladea, Liberation
Mono) are bundled for it. Licences recorded in `Fonts/FONT-LICENSES.md` — the RB-6 lesson
applied before anyone has to ask.

**A fixture that only passes at one set of font metrics is testing the font.** Registering
the resolver changed the drawing face and two gate cases flipped: both asserted "a footnote
taller than a page is reported" using a fixture sized just past the boundary, which the more
compact face brought back under it. The mechanism was fine. The fixtures are now sized well
past a page rather than just over it.

**Faking a font face corrupts the text layer.** Bold inside footnotes was approximated by
double-striking the word, which does thicken the stem — and puts the word in the text layer
twice, so the footnote copied out as `Smith Smith v.v. Jones`. Reverted: emphasis is
reported as unrendered instead. Closing it properly needs bold and italic font files, which
is a licensing decision rather than an engineering one. Measured while doing this and worth
recording: PdfSharpCore has NO font resolver registered, so every family and every style
resolves to one identical face — `@page { @top-center { font-family } }` has never had any
effect either.

**An image can still carry a text layer.** A page float is drawn as a picture and pictures
carry no text, which cost floated tables their selectability, search and screen-reader
access. Re-drawing the float's own words over the image with a fully transparent brush, at
coordinates measured per line in the browser, restores all three and changes nothing
visually — the same technique a scanned document uses for OCR.

**`display: none` does not inherit, and that silently ate half a footnote.** Runs were
captured after the footnote was hidden; a hidden element's own text nodes are skipped while
text inside a `<b>` or `<a>` child survives, so the footnote came out reading
`Smith v. Jones2026 WL 1234the original filing`. Capture before mutating.

**Four defects that only a document using everything at once could find.** Building a
proof-sheet document that exercises every Tier-1 feature together surfaced four bugs that
every existing gate passed over, because each fixture had avoided the precise combination
that triggers them. All four are now pinned as REG-1..REG-4 in the typesetting gate.

1. **`text-transform` is applied when a page is painted, not when the DOM is read.** A
   heading badge styled `text-transform: uppercase` extracts from the PDF in capitals while
   the fingerprint taken from the document keeps the author's case, so the two never match.
   Nine of ten running headers named the wrong section. Fingerprint comparison now folds
   case.
2. **`@page :first` leaks into every part of a stitched document.** Named pages render the
   document in runs, and Chromium treats the first page of EVERY run as `:first`, so a
   cover's 70mm margin reappeared on the opening page of each later run. It is now
   cancelled on every part except the first.
3. **...and cancelling it naively breaks named pages.** `@page :first` outranks a bare
   `@page`, so a reset that re-asserted only the DEFAULT geometry turned a one-page
   landscape run back to portrait. The run's own geometry and the reserved bands must be
   restated at `:first` specificity too.
4. **A page float's text leaves the text layer.** It is redrawn as an image, so a
   fingerprint built from it can never match — the section holding a floated figure had its
   running header resolve to the table of contents. Floated subtrees are now excluded when
   fingerprints are built.

**Fingerprints must be built from RENDERED text, and matched with a graduated prefix.**
`textContent` has no idea where one element stops and the next begins, so a table came out
as `FeatureChromiumalone` against the PDF's spaced words and matched nothing. `innerText`
inserts the breaks the layout actually has. And extracted reading order is not visual
order: a two-column section comes out of the page interleaved line by line, so a long
fingerprint stops matching part-way through even though it is unmistakably on that page.
The resolver therefore tries the whole fingerprint, then progressively shorter prefixes,
all anchored at the element's own text — losing reach, never precision.

**Engine-drawn boxes have to be aligned to the MEASURED text column.** Running headers,
footnote bands and page floats were all positioned from the caller's margin options, which
a document that leaves its margins to `@page` CSS sends through as zero. Every one of them
was then drawn at a 36pt fallback inset while the body text sat at the CSS margin, visibly
out of line with the column it belonged to.

**Group PDF words into lines by BASELINE, never by the bounding box.** A word's box tracks
its actual glyphs, so on a single line one word's top and bottom can sit several points away
from its neighbour's. Bucketing on either splits one visual line across buckets and
interleaves the reading order — which silently produced anchor text that exists nowhere in
the document. Every word on a line shares a baseline, and nothing else about the box.

**Matching PDF text back to the DOM needs whitespace squashed and hidden content excluded.**
The two disagree at exactly the word boundaries: a footnote call extracts as `golf1` but
lives in the DOM as a separate `<sup>`, and joining extracted words with single spaces
invents gaps the DOM never had. Worse, a hidden footnote body still sits inside the
paragraph that referenced it, so `textContent` splices a whole footnote into the middle of
the sentence being matched. Comparing whitespace-free text over visible nodes only makes
both problems disappear.

**Anything document-wide has to be resolved against the FINISHED document.** Named pages
render the document in parts, and every feature that counts pages had to be pointed at the
stitched result rather than at a part: page counters, cross-references, footnote and float
placement, and the bookmark outline. The outline was the one that would have shipped wrong
silently — the planner counts heading pages in a single continuous DOM pass, which simply
stops being true once the document is rendered in pieces, so those pages are now re-derived
from the merged PDF with the same fingerprint matcher as everything else.

**Measure the page margin from a page that is actually full.** The reserved-band base falls
back to measuring where content stops when the caller sets no margin. Measuring a page that
holds two lines reports the whole empty half of it as margin: a footnote landed 42% down the
page, and the reservation computed from it grew so large that a two-page document came out
as four. Pages whose content spans less than half the sheet are now skipped, which is safe
because a short page is precisely the case with room to spare.

**The sanitizer is the recurring trap.** Three separate features were broken by it, all the
same way: the sanitizer necessarily drops CSS it does not recognise, and GCPM constructs
are by definition the ones it does not recognise. `@page { size }` was silently stripped
(every document rendered A4); `string-set`, `leader()` and `@page` margin boxes were too.
The fix for the latter three was to parse GCPM from the **pre-sanitization** source
(`RenderingContext.OriginalHtml`) — reading authored intent only, never re-injecting
markup. Any future paged-media property should assume this problem exists.

**Page numbers always come from the real PDF.** Running headers resolve the page of each
`string-set` assignment with the same fingerprint matcher as cross-references, not from DOM
geometry. DOM-geometry estimation was already proven wrong twice for cross-references, and
a header naming the wrong chapter is exactly as wrong as a ToC naming the wrong page.

**Two bugs the gate could not have caught, but unit tests did.** The GCPM parser mis-trimmed
`::after` (producing the invalid selector `.toc a:`, which threw and returned HTTP 500 for
the whole document), and captured surrounding markup when a GCPM rule was the FIRST rule in
a stylesheet (yielding `<html><head><style>h1`, matching nothing, failing silently). Gate
fixtures happened to avoid both. Browser-free unit tests on the parser are what found them.
