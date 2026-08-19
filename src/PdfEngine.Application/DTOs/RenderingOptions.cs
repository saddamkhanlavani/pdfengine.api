using System.Collections.Generic;

namespace PdfEngine.Application.DTOs;

public class RenderingOptions
{
    public string PageSize { get; set; } = "A4";
    public string MarginTop { get; set; } = "0px";
    public string MarginBottom { get; set; } = "0px";
    public string MarginLeft { get; set; } = "0px";
    public string MarginRight { get; set; } = "0px";
    public bool PrintBackground { get; set; } = true;
    public bool DisplayHeaderFooter { get; set; } = false;
    public string? HeaderTemplate { get; set; }
    public string? FooterTemplate { get; set; }

    // Page geometry. PreferCSSPageSize defaults true so a document's own `@page` CSS
    // (size, margins) governs the output unless the caller pins PageSize/margins here.
    public bool Landscape { get; set; } = false;
    public double Scale { get; set; } = 1.0;
    public string? PageRanges { get; set; }
    public bool PreferCSSPageSize { get; set; } = true;
    public string? ReferenceScreenshotBase64 { get; set; }
    public bool CaptureScreenshot { get; set; } = false;
    public bool CaptureHAR { get; set; } = false;
    
    // PDF Metadata
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }

    // Render-wait strategy for charts/canvas/animated content. Without an explicit wait,
    // a canvas chart mid-animation at print time captures blank — the standing project
    // defect this exists to fix. Both are optional and additive to the normal page-load wait.
    public string? WaitForSelector { get; set; }
    public int RenderDelayMs { get; set; } = 0;

    // Polls a JS expression until it's truthy (e.g. "window.chartsReady === true")
    // — the same mechanism PDFShift's `wait_for` and Gotenberg's `waitForExpression`
    // expose, for callers whose chart/render-completion signal isn't a DOM selector
    // appearing but a JS-level flag/promise settling. Requires AllowScripts is
    // irrelevant here — this evaluates in the page context regardless, same as
    // WaitForSelector; only injecting new <script> tags requires AllowScripts.
    public string? WaitForFunction { get; set; }

    // Navigation completion signal, matching Playwright's own WaitUntilState names.
    // "load" (default) fires once the load event completes; "networkidle" waits
    // until no network connections for 500ms — more reliable for pages whose
    // charts/widgets fetch data asynchronously after the initial page load, at the
    // cost of added render latency. "domcontentloaded" is the fastest, for content
    // that needs no external resources at all.
    public string WaitUntil { get; set; } = "load";

    // Bookmarks/outline generated from <h1>-<h6> headings, safe to leave on by default —
    // it only adds a navigation panel entry, never changes visible page content.
    public bool GenerateOutlineFromHeadings { get; set; } = true;

    // PDF encryption (owner/user password + permission flags), applied via PdfSharpCore
    // in the same post-process pass as metadata/outline. Null passwords mean "not set".
    public string? OwnerPassword { get; set; }
    public string? UserPassword { get; set; }
    public bool AllowPrinting { get; set; } = true;
    public bool AllowCopyContent { get; set; } = true;
    public bool AllowAnnotations { get; set; } = true;

    // PdfSharpCore exposes 8 real, independent PDF permission bits — the engine
    // previously only wired up 3 (print/copy/annotate). These five complete the
    // set: modifying page content, filling in existing form fields, extracting
    // content for accessibility tools (legally distinct from general copying in
    // most jurisdictions), assembling/reordering/splitting pages, and high-res
    // vs. degraded-quality printing.
    public bool AllowModifyContents { get; set; } = true;
    public bool AllowFillingForms { get; set; } = true;
    public bool AllowAccessibilityExtract { get; set; } = true;
    public bool AllowAssembleDocument { get; set; } = true;
    public bool AllowFullQualityPrinting { get; set; } = true;

    // Diagonal text watermark stamped on every page during the same post-process pass.
    public string? WatermarkText { get; set; }

    // Off by default: the sanitizer strips ALL <script> tags, which is the correct
    // default for rendering HTML that might include untrusted/third-party content, but
    // it also means Chart.js/D3/canvas-drawing scripts cannot run at all. Set this to
    // true only when the HTML source is trusted (your own templates, not raw
    // user-submitted content) and you need JS-driven charts or dynamic rendering.
    // Inline event-handler attributes (onclick=, onerror=, etc.) and javascript: URIs
    // are still stripped either way — this only affects <script> tags themselves.
    public bool AllowScripts { get; set; } = false;

    // For Url-mode requests only: cookies and custom headers so an authenticated page
    // (behind a login, an internal preview link, etc.) can actually be rendered, not
    // just publicly-reachable pages. Cookies are scoped to the request's Url host.
    public Dictionary<string, string>? Cookies { get; set; }
    public Dictionary<string, string>? ExtraHttpHeaders { get; set; }

    // Renders the whole document as one continuous page sized to fit all of its
    // content, instead of splitting into fixed-size pages — useful for chat
    // transcripts, single invoices, or anything meant to be read as one long sheet
    // rather than paginated. PageSize/margins still set the *width*; only the height
    // is computed from actual content.
    public bool FullHeight { get; set; } = false;

    // Image watermark (base64-encoded PNG/JPEG), stamped centered on every page
    // during the same post-process pass as the text watermark. Independent of
    // WatermarkText — either or both may be set.
    public string? WatermarkImageBase64 { get; set; }
    public double WatermarkImageOpacity { get; set; } = 0.15;

    // Extra CSS/JS appended after the document loads (HTML or Url mode alike) without
    // having to hand-edit the source — e.g. a shared print stylesheet. ExtraJs only
    // runs when AllowScripts is also true, for the same reason inline <script> tags
    // require it: this executes in the page with full DOM access.
    public string? ExtraCss { get; set; }
    public string? ExtraJs { get; set; }

    // Re-encodes inline base64 images (opaque -> JPEG, transparent -> WebP) at
    // ImageQuality instead of shipping whatever bytes the author embedded verbatim.
    // Off by default since it's a lossy transform the caller should opt into.
    public bool OptimizeImages { get; set; } = false;
    public int ImageQuality { get; set; } = 82;

    // Chromium's native tagged-PDF export (Playwright Page.PdfAsync's Tagged/Outline
    // options) — a real /StructTreeRoot with correct heading/table/figure/list
    // semantics, verified to survive the PdfSharpCore post-process pass intact.
    // This is the foundation for accessible (PDF/UA-style) output; it is NOT itself
    // a certified-conformant guarantee — conformance still depends on how semantic
    // the source HTML is. Off by default: it changes the PDF's internal structure
    // and there's no reason to pay that cost for callers who don't need it.
    public bool GenerateTaggedPdf { get; set; } = false;

    // PDF/A archival conformance level — null means "don't attempt it" (default).
    // Accepted values: "PDF/A-2b", "PDF/A-3b". PDF/A-1 is deliberately not offered:
    // its stricter no-transparency rule is routinely violated by ordinary CSS
    // (shadows, gradients, opacity) that Chromium's print output uses, so claiming
    // PDF/A-1 support here would be a real overclaim. PDF/A forbids encryption by
    // spec — setting both together is a validation error, not silently resolved.
    public string? PdfaCompliance { get; set; }

    // Auto-populates real page-number cross-references — the CSS GCPM
    // `target-counter()` capability Chromium's engine has no native concept of
    // (pages don't exist outside of print). Mark a target with an `id`, then any
    // element with `data-pdfengine-pageref="that-id"` gets its text replaced with
    // the real page number the target landed on, computed from the same pagination
    // pass that already drives the outline. Enables a genuine "Contents ... 4"
    // style table of contents or "see page 12" cross-reference without guessing
    // page numbers by hand. On by default — a no-op unless the HTML actually uses
    // the data-pdfengine-pageref attribute.
    public bool EnablePageReferences { get; set; } = true;

    // Minimum lines of a text block kept together across a page break: `orphans` is the
    // minimum left at the BOTTOM of a page, `widows` the minimum carried to the TOP of
    // the next. A single line stranded alone is the classic mark of an unedited document.
    //
    // Measured: Chromium's paged-media engine implements both correctly — with
    // orphans/widows of 4, a paragraph that would have split 5/3 was moved WHOLE to the
    // next page. The engine's job is therefore to expose them (they were previously
    // hard-coded to 2 with no caller control) and to REPORT the cases CSS cannot fix,
    // rather than to re-implement line breaking.
    //
    // Raising these trades page utilization for typographic quality: a higher value moves
    // more blocks wholesale to the next page, leaving more whitespace behind.
    public int Orphans { get; set; } = 2;
    public int Widows { get; set; } = 2;

    // GCPM footnotes (T1-5): `float: footnote` elements are lifted out of the text flow,
    // replaced in place by a call marker, and drawn into a reserved band at the bottom of
    // the page their call landed on.
    //
    // Measured 2026-08-18: Chromium supports NONE of this — `float: footnote` content
    // renders inline exactly where it was authored, and `::footnote-call` /
    // `::footnote-marker` produce no numbering at all. So unlike orphans/widows this is
    // not a pass-through; the engine relocates the content itself.
    //
    // This costs re-renders. Reserving bottom space changes pagination, which can move a
    // call to a different page, so the engine iterates until every page holding a call
    // has room for its footnotes (bounded — see MaxFootnoteReflowPasses). Turning this
    // off leaves `float: footnote` to Chromium, which renders it inline.
    // A no-op for the documents (the vast majority) that declare no footnotes.
    public bool EnableFootnotes { get; set; } = true;

    // GCPM page floats (T1-8): `float: top` / `float: bottom` pull an element to the top
    // or bottom edge of the page it was authored on, instead of leaving it mid-paragraph.
    //
    // Measured 2026-08-18: Chromium renders BOTH exactly where authored — a `float: top`
    // box and a `float: bottom` box landed at the identical 38% down page 1 — so, like
    // footnotes, the engine relocates the content itself.
    //
    // The floated element is CAPTURED AS AN IMAGE while the browser still has it laid
    // out, and that image is drawn into the reserved band. Arbitrary content cannot be
    // redrawn from text the way a footnote can. Content with real text in it therefore
    // loses its text layer, which is reported per render rather than left to be
    // discovered in the extracted output.
    // A no-op for documents that declare no page floats.
    public bool EnablePageFloats { get; set; } = true;

    // GCPM named pages (T1-7): `@page cover { size: A4 landscape; margin: 50mm }` plus
    // `.cover { page: cover }` gives one section its own paper size, orientation and
    // margins — a landscape fold-out in a portrait report, a wide-margin cover.
    //
    // Measured 2026-08-18: Chromium silently ignores `page: <name>` outright. Unlike
    // running headers this cannot be fixed after the render, because page geometry changes
    // LAYOUT and not just what is stamped on top of it. Each run of consecutive content
    // sharing a page name is therefore rendered separately and the parts are stitched into
    // one document.
    //
    // The cost is one extra render per run, and it is a no-op for documents that declare
    // no named pages.
    public bool EnableNamedPages { get; set; } = true;

    // How the space for footnotes and bottom page floats is reserved (T1-9).
    //
    //   "auto"     (default) — the engine decides, per document, after it has measured it.
    //                Whether per-page pays off depends on how UNEVENLY the footnotes are
    //                spread, which is a property of the document that a caller cannot see
    //                from the HTML but the engine can measure exactly after the first
    //                render: uniform sacrifices the tallest band on every page, per-page
    //                sacrifices each page's own band and nothing on pages without one.
    //                It takes per-page when the difference is worth roughly half a page of
    //                height or more AND the extra renders fit the budget, and REPORTS which
    //                it chose and why on every render that has footnotes.
    //
    //   "uniform"  — the bottom margin grows document-wide by the largest band
    //                any single page needs, and Chromium re-paginates once. One or two
    //                extra renders for any document. Pages carrying few or no footnotes
    //                lose the same band as the busiest page.
    //
    //   "per-page" — each page reserves only what it actually needs, by forcing a page
    //                break before the content that would be overrun. Tighter output, and
    //                materially more expensive: **one extra render per page that needs a
    //                band**, because a break on page N shifts every later page and makes
    //                every other page's measurement, taken from the same render, stale.
    //                That was measured, not assumed — applying several breaks from one
    //                render produced pages holding a single paragraph each.
    //
    // Two further measured constraints shape this. `@page :nth(N)` would make per-page
    // reservation native and free; Chromium ignores it (verified — `:nth(2)` and `:nth(2n)`
    // both produced output identical to no rule at all, while `:first` correctly
    // re-paginated). And reserving at the TOP of a page cannot be done by breaking, since
    // content cannot be pushed upwards, so `float: top` keeps the uniform reservation in
    // both modes.
    //
    // Per-page mode is bounded by MaxPerPageReservationPasses and falls back to a uniform
    // reservation for whatever it has not settled by then — never to overlapping text.
    public string FootnoteReservationMode { get; set; } = "auto";

    // Pass budget for "per-page" mode. Each pass places one page's band and costs one
    // render, so this is also the maximum number of pages that can be tightened. Running
    // out is REPORTED and the remainder falls back to the uniform reservation.
    public int MaxPerPageReservationPasses { get; set; } = 12;

    // Upper bound on the reflow loop shared by footnotes and page floats. Each pass is
    // one extra render. Reaching the bound is REPORTED rather than silently accepted,
    // because the failure it indicates — a page whose reserved band does not fit what was
    // placed in it — is exactly the kind of silent degradation this engine surfaces.
    public int MaxFootnoteReflowPasses { get; set; } = 4;

    // --- T2-3: interactive form fields --------------------------------------------

    // Fillable fields placed on the rendered page. The backlog had this BLOCKED on the PDF
    // library, and re-measuring showed why that was the wrong conclusion: PdfSharpCore and
    // PDFsharp both expose `PdfTextField` with zero public constructors, but the FORMAT is
    // not blocked at all — a field is a widget annotation plus an entry in the catalog's
    // AcroForm, and both are ordinary dictionaries. The library's convenience API was
    // missing, not the capability.
    //
    // Fields are declared with `NeedAppearances`, which asks the reader to draw them. That
    // is what makes them look native in each viewer, and it is also why forms cannot be
    // combined with PDF/A: archival conformance requires baked appearance streams and
    // embedded fonts, and the two requirements contradict each other.
    public List<PdfFormField> FormFields { get; set; } = new();

    // --- T2-6: print production (bleed, crop marks, page boxes) --------------------

    // Bleed is the margin of artwork that runs PAST the finished page edge so that a
    // trimming blade landing a fraction out still cuts through ink rather than leaving a
    // white sliver. It has to exist at RENDER time — the page is rendered larger so
    // backgrounds actually extend into it — and the finished size is then recorded in the
    // PDF's TrimBox.
    //
    // PageSize stays the FINISHED (trimmed) size. Set 3mm here and an A4 job renders at
    // 216x303mm with an A4 TrimBox inside it, which is what a printer expects.
    public double BleedMm { get; set; }

    // Crop marks are the corner rules a printer cuts to. They sit outside the bleed, so
    // requesting them adds a further margin of sheet around it.
    public bool CropMarks { get; set; }

    // NOTE on CMYK and PDF/X, deliberately absent: converting Chromium's RGB output to
    // CMYK needs a real colour-management pipeline, and the usual engine for it
    // (Ghostscript) is AGPL — a licensing decision, not an engineering one, and exactly the
    // class of problem RB-6 existed to avoid. PDF/X conformance is not claimed either,
    // because no validator available here checks it and an unverified conformance claim is
    // worse than none. Bleed, crop marks and the page boxes below ARE independently
    // measurable, so they are what is offered.

    // --- T2-5: linearization (fast web view) --------------------------------------

    // Reorders the file so a reader can display page 1 from the first bytes it receives,
    // instead of waiting for the whole document. It only pays off when the PDF is served
    // over HTTP with range requests and is large enough for the wait to be noticeable, so
    // it is off by default rather than a free win.
    //
    // Linearizing is a structural rewrite of the whole file, so it runs after everything
    // that changes bytes and before signing (which seals them). It requires the `qpdf`
    // binary; if it is missing the request FAILS rather than quietly returning an
    // unlinearized file, because "I asked for fast web view and did not get it" is exactly
    // the kind of silent degradation that is impossible to notice.
    public bool Linearize { get; set; }

    // --- T2-2: digital signatures -------------------------------------------------

    // A PKCS#12 (.pfx/.p12) bundle carrying the signing certificate AND its private key,
    // base64-encoded, with its password. Supplying a private key over an API is a real
    // security surface: it is never logged, never echoed in diagnostics, and never stored.
    //
    // The signature is INVISIBLE — it seals the document and appears in the reader's
    // signature panel, but draws nothing on the page. A visible appearance would need a
    // font resolver registered for PDFsharp as well (a separate one from the resolver the
    // engine registers for PdfSharpCore), which is not wired up.
    //
    // Signing and encryption cannot be combined: signing seals a byte range, and encrypting
    // afterwards rewrites those bytes. Requesting both is a validation error rather than a
    // document whose signature silently fails to verify.
    public string? SigningCertificateBase64 { get; set; }
    public string? SigningCertificatePassword { get; set; }

    /// <summary>Why the document was signed, shown in the signature panel.</summary>
    public string? SignatureReason { get; set; }
    public string? SignatureLocation { get; set; }

    // An RFC 3161 timestamp authority. Supplying one raises the signature from a basic
    // signature to PAdES B-T, and the difference is not cosmetic: without a trusted
    // timestamp, the only evidence of WHEN the document was signed is a clock the signer
    // controls, and the signature stops being verifiable the day the certificate expires.
    // With one, it remains verifiable afterwards because the timestamp proves the
    // certificate was valid at the moment of signing.
    //
    // Free public authorities exist (for example http://timestamp.digicert.com). It is
    // opt-in because it makes signing depend on a network call to a third party, which is
    // a decision for the caller rather than a default the engine takes for them.
    public string? TimestampUrl { get; set; }

    // Embeds the evidence a verifier needs to check the signature years later — the
    // certificate chain and the revocation lists saying those certificates were still good
    // when it was signed. This is what raises PAdES B-T to B-LT.
    //
    // The difference is concrete: a B-T signature still requires reaching the issuing
    // authority at verification time, and authorities retire endpoints. A B-LT document
    // carries its own evidence and can be validated from an archive with no network at all.
    // Appended as an incremental update, so the bytes the signature seals are untouched.
    public bool EmbedValidationData { get; set; }

    // There is deliberately no SignatureContactInfo. PDFsharp accepts one on its options
    // and never writes it — measured, the signature dictionary comes out carrying /Reason
    // and /Location and no /ContactInfo — and the dictionary is only created during save,
    // so it cannot be set beforehand, while adding the key afterwards would shift the bytes
    // the signature seals. An option that reliably does nothing is worse than no option.

    // --- T2-1: attachments / embedded files ---------------------------------------

    // Files carried INSIDE the PDF. The commercial driver is EU e-invoicing: Factur-X and
    // ZUGFeRD are a PDF/A-3 document with the machine-readable invoice XML embedded in it,
    // so the human reads the page and the buyer's system reads the attachment — one file,
    // both audiences.
    //
    // PDF/A-3 is the conformance level that permits arbitrary embedded files. PDF/A-2
    // does NOT, and asking for both is a validation error rather than something quietly
    // resolved, for the same reason encryption plus PDF/A is.
    public List<PdfAttachment> Attachments { get; set; } = new();

    // --- Determinism controls (Release Gate J) ------------------------------------
    // The failure these exist to prevent: Chromium updates, and last month's invoice
    // silently reflows. Rendering is only reproducible if everything ambient — the
    // clock, the timezone, the locale, the random seed, the engine build — is either
    // pinned or reported. Each of these is null/off by default, so behaviour is
    // unchanged unless a caller opts into reproducibility.

    // Freezes the page clock. `new Date()`, `Date.now()` and `performance.now()` all
    // report this instant, so a template printing "generated on ..." or a chart library
    // seeding animation from the clock produces byte-stable output across runs.
    public DateTime? FixedDateUtc { get; set; }

    // Replaces Math.random with a seeded generator. Chart libraries commonly use
    // randomness for jitter, IDs and animation offsets, which defeats output hashing.
    public int? RandomSeed { get; set; }

    // IANA timezone (e.g. "UTC", "America/New_York") and BCP-47 locale (e.g. "en-GB").
    // These affect date/number formatting AND font fallback selection. Setting either
    // requires a dedicated browser context — Playwright fixes both at context creation —
    // so a render that sets them does not use the shared context and costs more.
    public string? Timezone { get; set; }
    public string? Locale { get; set; }

    // Assert the exact engine build. When set and it does not match the running engine,
    // the render FAILS rather than silently producing output from a different Chromium.
    // That failure is the entire point: a silent version change is the drift this gate
    // exists to catch. Accepts a full version ("2026.08+chromium133.0.6943.16") or a
    // profile-only prefix ("2026.08").
    public string? PinEngineVersion { get; set; }

    // NOTE (RB-2, CLOSED 2026-08-18): RTL logical-order text extraction is handled
    // automatically and has no option, because a text layer that cannot be copied or
    // searched is a defect rather than a preference. See
    // PlaywrightPdfService.ApplyActualTextToReversedRuns — it attaches /ActualText to
    // Chromium's /ReversedChars runs, and is inherently a no-op for documents with no
    // RTL text. A `NormalizeTextLayer` option briefly existed here to rewrite
    // /ToUnicode CMaps instead; it was measured as a no-op and removed.
}
