using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Playwright;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class PaginationPlanner : IPaginationPlanner
{
    // Standard CSS page sizes in px at 96 DPI, portrait (width, height). Must stay in
    // sync with GeneratePdfCommandValidator.AllowedPageSizes.
    private static readonly Dictionary<string, (double WidthPx, double HeightPx)> PageSizesPx = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A4"] = (793.7, 1122.5),
        ["Letter"] = (816, 1056),
        ["Legal"] = (816, 1344),
        ["A3"] = (1122.5, 1587.4),
        ["A5"] = (559.4, 793.7),
        ["A6"] = (396.9, 559.4),
        ["Tabloid"] = (1056, 1632),
        ["Ledger"] = (1632, 1056),
    };

    public async Task ExecuteAsync(RenderingContext context, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var html = context.Html;
        var plan = context.Plan;

        // Hoisted: used both by the in-browser measurement pass and by the injected
        // print stylesheet further down.
        var orphans = Math.Max(1, context.Options?.Orphans ?? 2);
        var widows = Math.Max(1, context.Options?.Widows ?? 2);

        cancellationToken.ThrowIfCancellationRequested();

        // Pass 2: Dynamic Page Balancing (runs once the page is loaded and laid out)
        if (context.Page is IPage page)
        {
            var printableHeightPx = ComputePrintableHeightPx(context.Options);

            // GCPM rules are parsed from the RAW stylesheet because Chromium discards
            // string-set / target-counter / leader / @page margin boxes before CSSOM —
            // getComputedStyle and cssRules report nothing for them. Margin boxes are
            // stashed on the plan for the per-page stamping pass; the rest are handed to
            // the browser as data below.
            // Parsed from the PRE-sanitization source: the sanitizer strips exactly these
            // constructs (same class of bug as `@page { size }`, fixed 2026-08-18).
            var gcpm = GcpmCssParser.Parse(
                string.IsNullOrEmpty(context.OriginalHtml) ? html : context.OriginalHtml);
            plan.MarginBoxes.Clear();
            plan.MarginBoxes.AddRange(gcpm.MarginBoxes);
            if (gcpm.FootnoteArea != null) plan.FootnoteArea = gcpm.FootnoteArea;

            // T1-5. Off means `float: footnote` is left to Chromium, which renders it
            // inline — the measured upstream behaviour, not a fallback we invented.
            var footnotesEnabled = (context.Options?.EnableFootnotes ?? true) && gcpm.Footnotes.Count > 0;

            // T1-7. A named page that only restyles a margin box needs no separate render,
            // so only geometry-changing definitions count as "in use".
            var namedPagesEnabled = (context.Options?.EnableNamedPages ?? true)
                && gcpm.NamedPageUses.Count > 0
                && gcpm.NamedPageUses.Any(u => gcpm.NamedPages.TryGetValue(u.Name, out var d) && d.ChangesGeometry);

            plan.NamedPages.Clear();
            if (namedPagesEnabled)
            {
                foreach (var kv in gcpm.NamedPages) plan.NamedPages[kv.Key] = kv.Value;
                plan.DefaultPage = gcpm.DefaultPage;
                plan.PseudoPagesWithGeometry = gcpm.PseudoPageGeometry.ToList();

                // Page parity restarts inside every part, so `:left`/`:right` mirrored
                // margins cannot survive stitching. Reported rather than silently mirrored
                // against the wrong side.
                var parity = gcpm.PseudoPageGeometry
                    .Where(x => x is "left" or "right").ToList();
                if (parity.Count > 0)
                {
                    context.Diagnostics.Warnings.Add(
                        $"Named page warning: this document uses both named pages and `@page :{string.Join("`/`:", parity)}` geometry. Named pages render the document in parts and page parity restarts in each part, so mirrored binding margins will not alternate correctly across the stitched document. Use named pages or `:left`/`:right`, not both.");
                }

                var undefined = gcpm.NamedPageUses
                    .Where(u => !gcpm.NamedPages.ContainsKey(u.Name))
                    .Select(u => u.Name).Distinct().ToList();
                if (undefined.Count > 0)
                {
                    context.Diagnostics.Warnings.Add(
                        $"Named page notice: `page: {string.Join(", ", undefined.Take(3))}` refers to a page name with no matching `@page` rule, so that content uses the document's default page geometry. Declare `@page {undefined[0]} {{ size: ...; margin: ... }}` to give it its own.");
                }
            }

            if (gcpm.RequestsPerPageFootnoteNumbering)
            {
                context.Diagnostics.Warnings.Add(
                    "Footnote notice: `counter-reset: footnote` on @page asks for per-page footnote numbering, which is NOT supported — the call marker is drawn into the page before the engine knows which page it landed on. Footnotes are numbered continuously through the document instead.");
            }

            var gcpmJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                targetCounters = gcpm.TargetCounters.Select(r => new { selector = r.Selector, targetAttr = r.TargetAttr, before = r.Before }),
                leaders = gcpm.Leaders.Select(r => new { selector = r.Selector, character = r.Character, before = r.Before }),
                stringSets = gcpm.StringSets.Select(r => new { selector = r.Selector, name = r.Name }),
                footnotes = footnotesEnabled
                    ? gcpm.Footnotes.Select(r => new { selector = r.Selector }).ToArray()
                    : Array.Empty<object>(),
                pageFloats = ((context.Options?.EnablePageFloats ?? true) && gcpm.PageFloats.Count > 0)
                    ? gcpm.PageFloats.Select(r => new { selector = r.Selector, edge = r.Edge }).ToArray()
                    : Array.Empty<object>(),
                namedPageUses = namedPagesEnabled
                    ? gcpm.NamedPageUses.Select(r => new { selector = r.Selector, name = r.Name }).ToArray()
                    : Array.Empty<object>(),
                footnoteCall = gcpm.FootnoteCall == null ? null : new
                {
                    content = gcpm.FootnoteCall.Content,
                    fontSizePt = gcpm.FootnoteCall.FontSizePt,
                    color = gcpm.FootnoteCall.Color
                },
                footnoteMarker = gcpm.FootnoteMarker == null ? null : new
                {
                    content = gcpm.FootnoteMarker.Content,
                    fontSizePt = gcpm.FootnoteMarker.FontSizePt,
                    color = gcpm.FootnoteMarker.Color
                }
            });

            var paginationScript = $@"
                (function() {{
                    const targetPageHeight = {printableHeightPx.ToString(System.Globalization.CultureInfo.InvariantCulture)};

                    // Direct body children give coarse-grained blocks (sections, divs).
                    // Headings/tables/keep-together elements are tracked wherever they're
                    // actually nested — real documents wrap content in <section>/<article>,
                    // and a body-direct-children-only selector would silently miss every
                    // heading and table inside one, disabling orphan-avoidance, forced
                    // table breaks, and outline generation for essentially any
                    // semantically-structured document.
                    const bodyChildren = Array.from(document.querySelectorAll('body > *:not(script):not(style)'));
                    const nestedSpecial = Array.from(document.querySelectorAll('h1, h2, h3, h4, h5, h6, table, .keep-together'))
                        .filter(el => el.parentElement !== document.body);
                    const blocks = bodyChildren.concat(nestedSpecial).sort((a, b) => {{
                        const pos = a.compareDocumentPosition(b);
                        if (pos & Node.DOCUMENT_POSITION_FOLLOWING) return -1;
                        if (pos & Node.DOCUMENT_POSITION_PRECEDING) return 1;
                        return 0;
                    }});

                    let currentPageTop = 0;
                    let currentPage = 1; // 1-based, matches PDF page numbering
                    let lastRenderedBottom = 0; // deepest point reached on the current page so far
                    const headingOutline = [];
                    const pageWhitespace = [];

                    // Standard orphan/widow semantics (matching the `orphans: 2` CSS rule
                    // applied elsewhere): a heading counts as 'not orphaned' once roughly
                    // two real lines of what actually follows it can join it on the same
                    // page. Two earlier attempts at this got it wrong in opposite
                    // directions: measuring the *entire* next block (e.g. a 400px+ chart)
                    // pushed headings needlessly and wasted whitespace; a flat 48px token
                    // guess was verified — by testing, not assumption — to under-count a
                    // real paragraph's own top margin plus two real text lines, letting a
                    // heading get stranded alone with its paragraph pushed away. This
                    // measures the *actual* gap and *actual* line-height of whatever
                    // really follows the heading in the DOM, so it's correct regardless
                    // of the document's font-size/spacing instead of a guessed constant.
                    const minFollowContentPx = 48;
                    const maxFollowContentPx = 140; // ceiling: don't demand more than ~2 generous lines' worth

                    // A heading sitting inside a CSS Grid/Flex row alongside sibling
                    // columns (e.g. two side-by-side ""card"" panels) is a layout
                    // element, not a standalone document heading — forcing
                    // break-before on it fragments just that one column while its
                    // sibling stays put, which reproducibly left an entire blank
                    // page between the two columns (verified: a two-card grid with
                    // an <h3> in the second card produced page N, a blank page
                    // N+1, then the second card alone on page N+2). Orphan
                    // avoidance only makes sense for headings that own a full
                    // page-width block of their own.
                    const isInMultiColumnLayout = (el) => {{
                        let node = el.parentElement;
                        for (let depth = 0; node && depth < 3; depth++, node = node.parentElement) {{
                            const style = window.getComputedStyle(node);
                            const isGrid = style.display === 'grid' || style.display === 'inline-grid';
                            const isRowFlex = (style.display === 'flex' || style.display === 'inline-flex') && style.flexDirection !== 'column' && style.flexDirection !== 'column-reverse';
                            if ((isGrid || isRowFlex) && node.children.length > 1) return true;
                        }}
                        return false;
                    }};

                    // currentPageTop/currentPage only ever advanced when THIS script
                    // forced a break — meaning any break the document's own CSS
                    // already declares (the extremely common per-section
                    // page-break-after:always pattern) was invisible to it.
                    // Reproduced directly: a
                    // two-section document where each section independently fit on
                    // one page rendered as 4 pages — a blank page plus an
                    // unnecessarily split section — purely because Pass 2's internal
                    // page-boundary bookkeeping drifted the moment the first native
                    // break fired and never resynced. Every decision after that point
                    // was computed against a stale page-top, not the real one.
                    const isForcedBreakValue = v => v === 'page' || v === 'always' || v === 'left' || v === 'right';
                    const idToPage = {{}}; // populated inline as blocks are processed below — see EnablePageReferences
                    let lastNativeBreakBoundary = null;
                    const findNativeBreakBoundary = (block) => {{
                        let node = block;
                        while (node && node !== document.body && node !== document.documentElement) {{
                            const style = window.getComputedStyle(node);
                            if (isForcedBreakValue(style.breakBefore) || isForcedBreakValue(style.pageBreakBefore)) return node;
                            const prev = node.previousElementSibling;
                            if (prev) {{
                                const prevStyle = window.getComputedStyle(prev);
                                if (isForcedBreakValue(prevStyle.breakAfter) || isForcedBreakValue(prevStyle.pageBreakAfter)) return prev;
                                return null; // nearest preceding sibling exists and doesn't force a break — that settles it
                            }}
                            node = node.parentElement; // first-child: the real 'previous thing' is determined one level up
                        }}
                        return null;
                    }};

                    // Reports wasted space regardless of WHY the break happened — a
                    // forced page-break-after:always the document's own CSS asked for
                    // wastes exactly as much real estate as one this script forces for
                    // orphan-avoidance, and previously only the latter was ever
                    // measured. An author who stacks several short, forced-break
                    // sections gets a near-empty page per section with zero feedback
                    // that anything's off — this closes that gap with an explicit,
                    // actionable reason per occurrence instead of a silent PDF.
                    const recordPageBreak = (newTop, reason) => {{
                        const wastedPx = Math.max(0, targetPageHeight - lastRenderedBottom);
                        if (wastedPx > targetPageHeight * 0.15) {{
                            pageWhitespace.push({{ page: currentPage, wastedPx: Math.round(wastedPx), reason }});
                        }}
                        currentPageTop = newTop;
                        currentPage++;
                        lastRenderedBottom = 0;
                    }};

                    blocks.forEach((block, idx) => {{
                        const nativeBoundary = findNativeBreakBoundary(block);
                        if (nativeBoundary && nativeBoundary !== lastNativeBreakBoundary) {{
                            lastNativeBreakBoundary = nativeBoundary;
                            recordPageBreak(block.getBoundingClientRect().top, 'a forced page break in the document\'s own CSS (page-break-after/before)');
                        }}

                        const rect = block.getBoundingClientRect();
                        const relativeTop = rect.top - currentPageTop;
                        const relativeBottom = relativeTop + rect.height;

                        if (relativeBottom > targetPageHeight) {{
                            const isHeading = ['H1', 'H2', 'H3', 'H4', 'H5', 'H6'].includes(block.tagName) && !isInMultiColumnLayout(block);
                            let forceBreak = false;

                            if (isHeading) {{
                                const remaining = targetPageHeight - relativeTop;
                                let requiredFollowPx = minFollowContentPx;
                                const nextEl = block.nextElementSibling;
                                if (nextEl) {{
                                    const nextRect = nextEl.getBoundingClientRect();
                                    const realGap = Math.max(0, nextRect.top - rect.bottom);
                                    const nextLineHeight = parseFloat(window.getComputedStyle(nextEl).lineHeight) || 20;
                                    requiredFollowPx = Math.min(maxFollowContentPx, Math.max(minFollowContentPx, realGap + (nextLineHeight * 2)));
                                }}
                                forceBreak = remaining < rect.height + requiredFollowPx;
                            }} else if ((block.classList.contains('keep-together') || block.tagName === 'TABLE') && !isInMultiColumnLayout(block)) {{
                                forceBreak = true;
                            }} else if (relativeTop > targetPageHeight * 0.8 && !block.querySelector('h1, h2, h3, h4, h5, h6, table') && !isInMultiColumnLayout(block)) {{
                                // Only applies to leaf-like blocks with no nested
                                // heading/table of their own — those are already handled
                                // precisely by the branches above, wherever they're
                                // actually nested. Verified by testing to be the real
                                // cause of the original whitespace bug: a <section>
                                // wrapper is itself a body-child block, and pushing the
                                // WHOLE wrapper just because it starts late in the page
                                // was silently overriding a perfectly good decision
                                // already made for the heading inside it.
                                forceBreak = true;
                            }}

                            if (forceBreak) {{
                                block.style.breakBefore = 'page';
                                // Marked so a later stage can UNDO it. These decisions are
                                // estimates made against the page height as it is right
                                // now; anything that later changes the content area — the
                                // footnote band is the one that does — invalidates them,
                                // and a stale forced break strands a single paragraph on
                                // a page of its own.
                                block.setAttribute('data-pdfengine-planner-break', '1');
                                recordPageBreak(rect.top, 'orphan/keep-together avoidance — a heading, table, or keep-together block did not fit and was moved whole rather than split');
                            }} else if (!block.querySelector('h1, h2, h3, h4, h5, h6, table, .keep-together')) {{
                                // A coarse section-wrapper block's own bounding rect spans
                                // ITS ENTIRE content, not just whatever actually lands on
                                // the current physical page — using it here previously
                                // made the fill measurement register as ~100% full
                                // whenever a section wrapper merely happened to be taller
                                // than one page, even though the real rendered content
                                // ended far earlier. Wrapper blocks are skipped; the real
                                // nested heading/table/leaf content inside them updates
                                // this precisely when each is visited in turn.
                                lastRenderedBottom = Math.max(lastRenderedBottom, Math.min(relativeBottom, targetPageHeight));
                            }}
                        }} else if (!block.querySelector('h1, h2, h3, h4, h5, h6, table, .keep-together')) {{
                            lastRenderedBottom = Math.max(lastRenderedBottom, relativeBottom);
                        }}

                        if (['H1', 'H2', 'H3', 'H4', 'H5', 'H6'].includes(block.tagName)) {{
                            headingOutline.push({{
                                text: (block.textContent || '').trim().slice(0, 200),
                                level: parseInt(block.tagName.substring(1), 10),
                                page: currentPage
                            }});
                        }}

                    }});

                    // GCPM target-counter() equivalent. Chromium's engine has no native
                    // concept of ""pages"" outside of print, so a cross-reference like
                    // ""see page 12"" cannot be resolved from the DOM the way Prince XML
                    // resolves it from its own paged layout.
                    //
                    // Two earlier attempts resolved this from DOM geometry and BOTH
                    // produced wrong page numbers, verified against the physical PDF:
                    // counting forced breaks alone missed pages created by ordinary
                    // content overflow, and adding a scroll-height division then
                    // mis-attributed pages because forced breaks make DOM scroll
                    // coordinates non-linear with respect to real page boundaries.
                    // Page numbers therefore CANNOT be known until the document has
                    // actually been paginated into a PDF.
                    //
                    // So this pass only COLLECTS the targets plus a text fingerprint of
                    // each anchor; PlaywrightPdfService renders once, locates each
                    // fingerprint in the real PDF, and re-renders with true numbers
                    // substituted (the same multi-pass approach Prince itself uses).
                    // PDF/UA accessibility pre-check. Chromium draws list markers
                    // (the bullet/number from list-style) WITHOUT tagging them or
                    // marking them as Artifacts, so any <ul>/<ol> with a visible
                    // marker fails PDF/UA-1 clause 7.1 test 3. Verified: the same
                    // document passes 106/0 with list-style:none and fails 105/1
                    // with a marker; a ::before substitute is also untagged, so
                    // there is no fix that preserves the marker's appearance.
                    // We therefore REPORT it instead of silently restyling the
                    // author's document.
                    let listsWithMarkers = 0;
                    let imagesWithoutAlt = 0;
                    if ({((context.Options?.GenerateTaggedPdf ?? false) ? "true" : "false")}) {{
                        document.querySelectorAll('ul, ol').forEach(el => {{
                            const st = window.getComputedStyle(el);
                            if (st.listStyleType && st.listStyleType !== 'none') listsWithMarkers++;
                        }});
                        document.querySelectorAll('img').forEach(el => {{
                            const alt = el.getAttribute('alt');
                            if (alt === null || alt.trim() === '') imagesWithoutAlt++;
                        }});
                    }}

                    // Rendering Doctor (Release Gate A): content that will not appear in
                    // the PDF must be REPORTED, never silently dropped. Both classes below
                    // produce a perfectly valid-looking PDF with content missing, which is
                    // the single worst failure mode for a document the caller will send to
                    // a customer without re-reading it.
                    const printableWidth = document.documentElement.clientWidth;
                    let overflowingElements = 0;
                    let widestOverflowPx = 0;
                    let offPageElements = 0;

                    document.querySelectorAll('body *').forEach(el => {{
                        const st = window.getComputedStyle(el);
                        if (st.display === 'none' || st.visibility === 'hidden') return;
                        const r = el.getBoundingClientRect();
                        if (r.width === 0 && r.height === 0) return;

                        // Horizontal overflow: anything extending past the printable width
                        // is cropped at the page edge. A tolerance of 2px avoids flagging
                        // sub-pixel rounding on full-width blocks.
                        const overhang = Math.round(r.right - printableWidth);
                        if (overhang > 2) {{
                            // Only count the element itself, not every ancestor that
                            // inherits the same overflowing width, or one wide table
                            // reports as dozens of findings.
                            const parentR = el.parentElement ? el.parentElement.getBoundingClientRect() : null;
                            if (!parentR || Math.round(parentR.right - printableWidth) <= 2) {{
                                overflowingElements++;
                                if (overhang > widestOverflowPx) widestOverflowPx = overhang;
                            }}
                        }}

                        // Positioned entirely outside the page box. Authors use off-screen
                        // positioning for screen-reader-only text, so this is reported
                        // rather than treated as an error.
                        if (st.position === 'absolute' || st.position === 'fixed') {{
                            if (r.right < 0 || r.bottom < 0 || r.left > printableWidth) {{
                                offPageElements++;
                            }}
                        }}
                    }});

                    // --- T1-6: line-box measurement --------------------------------
                    // Chromium honours orphans/widows, but it CANNOT satisfy them for a
                    // block that is itself taller than a page — there is no split that
                    // leaves enough lines on both sides, so it breaks anyway and the
                    // request is silently dropped. Measuring real line boxes is the only
                    // way to see that, and an unreportable typographic failure is exactly
                    // the kind of silent degradation this engine reports rather than hides.
                    const orphanSetting = {orphans};
                    const widowSetting = {widows};
                    let unsatisfiableBlocks = 0;
                    let widowOrphanRisks = 0;
                    let measuredLineBoxes = 0;

                    const lineRects = (el) => {{
                        try {{
                            const range = document.createRange();
                            range.selectNodeContents(el);
                            // Client rects are one per line box; merge by rounded top so
                            // inline children (<b>, <a>) don't count their line twice.
                            const tops = new Map();
                            for (const r of range.getClientRects()) {{
                                if (r.width === 0 || r.height === 0) continue;
                                const key = Math.round(r.top);
                                if (!tops.has(key)) tops.set(key, r);
                            }}
                            return Array.from(tops.values()).sort((a, b) => a.top - b.top);
                        }} catch (e) {{ return []; }}
                    }};

                    document.querySelectorAll('p, li, blockquote').forEach(el => {{
                        const rects = lineRects(el);
                        if (rects.length < 2) return;
                        measuredLineBoxes += rects.length;

                        const blockHeight = rects[rects.length - 1].bottom - rects[0].top;
                        if (blockHeight > targetPageHeight) {{
                            // Taller than a page: orphans/widows cannot be honoured here.
                            unsatisfiableBlocks++;
                            return;
                        }}
                        if (rects.length < orphanSetting + widowSetting) {{
                            // Too few lines to split legally, so if a boundary falls
                            // inside it the browser must move the WHOLE block — correct,
                            // but it is the cause of whitespace worth attributing.
                            widowOrphanRisks++;
                        }}
                    }});

                    // --- GCPM (T1-1/T1-2/T1-3) -------------------------------------
                    // Chromium implements none of these, and drops the declarations
                    // before CSSOM, so the rules were parsed from the raw stylesheet in
                    // C# and are injected here as data.
                    const gcpm = {gcpmJson};

                    // A malformed authored selector must degrade to 'this one rule does
                    // nothing', never to a failed render. An unguarded querySelectorAll
                    // threw on a bad selector and returned HTTP 500 for the whole
                    // document — one CSS typo taking down the entire request.
                    const gcpmSelect = (selector) => {{
                        try {{ return Array.from(document.querySelectorAll(selector)); }}
                        catch (e) {{ return []; }}
                    }};

                    // target-counter(attr(href), page) -> reuse the proven cross-reference
                    // resolver rather than inventing a second page-number mechanism.
                    gcpm.targetCounters.forEach(rule => {{
                        gcpmSelect(rule.selector).forEach(el => {{
                            const raw = rule.targetAttr === 'href'
                                ? (el.getAttribute('href') || '')
                                : rule.targetAttr;
                            if (!raw.startsWith('#')) return;
                            const span = document.createElement('span');
                            span.setAttribute('data-pdfengine-pageref', raw.slice(1));
                            rule.before ? el.insertBefore(span, el.firstChild) : el.appendChild(span);
                        }});
                    }});

                    // leader('.') -> a flex filler that repeats the character and clips.
                    // The containing line is forced to flex because a leader only means
                    // anything if it can absorb the remaining width.
                    gcpm.leaders.forEach(rule => {{
                        gcpmSelect(rule.selector).forEach(el => {{
                            const span = document.createElement('span');
                            span.setAttribute('data-pdfengine-leader', '1');
                            span.textContent = rule.character.repeat(400);
                            span.style.cssText = 'flex:1 1 auto;overflow:hidden;white-space:nowrap;' +
                                'display:inline-block;min-width:1em;';
                            rule.before ? el.insertBefore(span, el.firstChild) : el.appendChild(span);
                            const line = el.closest('li, p, div, tr') || el.parentElement;
                            if (line && window.getComputedStyle(line).display.indexOf('flex') === -1) {{
                                line.style.display = 'flex';
                                line.style.alignItems = 'baseline';
                            }}
                        }});
                    }});

                    // --- Shared placement helpers (T1-5 footnotes, T1-8 page floats) ---
                    // Both features lift an element out of the flow and have to find the
                    // page its ORIGINAL position landed on. Neither element's own text is
                    // a usable fingerprint — a footnote call is a digit, and a floated
                    // figure often has no text at all — so what locates them is the text
                    // that surrounds them.
                    const norm = (t) => (t || '').normalize('NFKC').replace(/\s+/g, ' ').trim();
                    const BLOCKISH = 'p, li, td, th, blockquote, h1, h2, h3, h4, h5, h6, div, section, article, figure';

                    // `innerText` and not `textContent`, because a fingerprint is compared
                    // against text extracted from the RENDERED PDF. textContent has no idea
                    // where one element stops and the next begins, so a table comes out as
                    // 'FeatureChromiumalone' against the PDF's spaced words and matches
                    // nothing — measured, a page float fingerprinted from a table resolved
                    // to no page at all and fell back to page 1. innerText inserts the
                    // breaks the layout actually has, and skips hidden content for free.
                    const readableText = (el) => {{
                        if (!el) return '';
                        try {{
                            // The float element ITSELF, not just one containing a float:
                            // querySelector only looks at descendants, so without this the
                            // float's own caption still reached the fingerprint.
                            if (el.hasAttribute && el.hasAttribute('data-pdfengine-pagefloat')) return '';
                            // A page float is about to be lifted out and redrawn as an
                            // image, so its text will NOT be in the finished PDF's text
                            // layer. Including it produces a fingerprint that can never
                            // match — measured, the section containing a floated figure
                            // resolved to the table of contents instead of its own page.
                            if (el.querySelector && el.querySelector('[data-pdfengine-pagefloat]')) {{
                                let out = '';
                                const walk = (node) => {{
                                    if (node.nodeType === 3) {{ out += ' ' + node.nodeValue; return; }}
                                    if (node.nodeType !== 1) return;
                                    if (node.hasAttribute('data-pdfengine-pagefloat')) return;
                                    for (const child of node.childNodes) walk(child);
                                }};
                                walk(el);
                                return norm(out);
                            }}
                            return norm(el.innerText || el.textContent);
                        }}
                        catch (e) {{ return norm(el.textContent); }}
                    }};

                    // Text of the content that precedes a node in document order, walking
                    // up and out of wrappers. A block-level float or a footnote authored
                    // as its own block has no surrounding text inside its own block, and
                    // without this it would be unplaceable.
                    const precedingText = (startNode) => {{
                        let node = startNode, guard = 0, collected = '';
                        while (collected.length < 90 && guard++ < 40) {{
                            let prev = node.previousElementSibling;
                            while (!prev) {{
                                node = node.parentElement;
                                if (!node || node === document.body || node === document.documentElement) {{
                                    return norm(collected);
                                }}
                                prev = node.previousElementSibling;
                            }}
                            collected = readableText(prev) + ' ' + collected;
                            node = prev;
                        }}
                        return norm(collected);
                    }};

                    const surroundingFingerprint = (el) => {{
                        const own = readableText(el);
                        // `el.closest` would return the element itself when it IS a block,
                        // leaving no surrounding text to fingerprint — start at its parent.
                        const parentBlock = (el.parentElement && el.parentElement.closest(BLOCKISH)) || null;
                        let before = '', after = '';
                        if (parentBlock) {{
                            const blockText = readableText(parentBlock);
                            const at = own ? blockText.indexOf(own) : -1;
                            if (at > 0) before = blockText.slice(Math.max(0, at - 70), at);
                            if (at >= 0) after = blockText.slice(at + own.length, at + own.length + 70);
                        }}
                        if (before.trim().length < 12) {{
                            const walked = precedingText(el);
                            if (walked.length >= 12) before = walked.slice(-70);
                        }}
                        if (!before.trim() && !after.trim()) {{
                            // Nothing at all precedes it — the very first thing in the
                            // document. What FOLLOWS is on the same page, so it locates
                            // the element just as well.
                            let node = el.nextElementSibling, guard = 0, collected = '';
                            while (node && collected.length < 90 && guard++ < 25) {{
                                collected += ' ' + readableText(node);
                                node = node.nextElementSibling;
                            }}
                            after = norm(collected).slice(0, 70);
                        }}
                        const primary = before.trim().length >= 12 ? before.trim() : after.trim();
                        const shortFp = primary.length > 30
                            ? (primary === before.trim() ? primary.slice(-30) : primary.slice(0, 30))
                            : primary;
                        return {{ primary: primary.slice(0, 90), shortFp: shortFp }};
                    }};

                    const inDocumentOrder = (list) => list.sort((a, b) => {{
                        const pos = a.compareDocumentPosition(b);
                        if (pos & Node.DOCUMENT_POSITION_FOLLOWING) return -1;
                        if (pos & Node.DOCUMENT_POSITION_PRECEDING) return 1;
                        return 0;
                    }});

                    // --- Text-run extraction (T1-5 styling, T1-8 text layer) --------
                    // Both features redraw content the browser laid out: a footnote as
                    // real text, a page float as an image with a text layer over it.
                    // Neither can use textContent, because what has to survive is the
                    // STYLE and the POSITION of each stretch of text, not just the letters.

                    // Splits one text node into its visual lines. Ranges give a rect per
                    // line but not the text on each, so the end of every line is found by
                    // binary search on character offsets — the alternative, one rect for
                    // the whole node, would put a wrapped caption's text layer on a single
                    // line that no longer lines up with the picture underneath it.
                    const lineRunsOf = (node) => {{
                        const text = node.nodeValue || '';
                        const out = [];
                        if (!text.trim()) return out;
                        const range = document.createRange();
                        const visible = (r) => r.width > 0.5 && r.height > 0.5;
                        let start = 0, guard = 0;
                        while (start < text.length && guard++ < 400) {{
                            range.setStart(node, start); range.setEnd(node, text.length);
                            const rects = Array.from(range.getClientRects()).filter(visible);
                            if (rects.length === 0) break;
                            if (rects.length === 1) {{
                                out.push({{ text: text.slice(start), rect: rects[0] }});
                                break;
                            }}
                            const firstTop = Math.round(rects[0].top);
                            let lo = start + 1, hi = text.length, best = start + 1;
                            while (lo <= hi) {{
                                const mid = (lo + hi) >> 1;
                                range.setStart(node, start); range.setEnd(node, mid);
                                const rr = Array.from(range.getClientRects()).filter(visible);
                                const sameLine = rr.length <= 1 || Math.round(rr[rr.length - 1].top) === firstTop;
                                if (sameLine) {{ best = mid; lo = mid + 1; }} else {{ hi = mid - 1; }}
                            }}
                            range.setStart(node, start); range.setEnd(node, best);
                            const rr = Array.from(range.getClientRects()).filter(visible);
                            if (rr.length) out.push({{ text: text.slice(start, best), rect: rr[0] }});
                            if (best <= start) break;
                            start = best;
                        }}
                        return out;
                    }};

                    const textNodesOf = (root) => {{
                        const nodes = [];
                        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null, false);
                        let n;
                        while ((n = walker.nextNode())) {{
                            if (!n.nodeValue || !n.nodeValue.trim()) continue;
                            const parent = n.parentElement;
                            if (!parent) continue;
                            const st = window.getComputedStyle(parent);
                            if (st.display === 'none' || st.visibility === 'hidden') continue;
                            nodes.push(n);
                        }}
                        return nodes;
                    }};

                    // T1-5: the footnote's text split by STYLE, so emphasis and links can
                    // be drawn rather than flattened away.
                    const styledRunsOf = (root) => {{
                        const runs = [];
                        textNodesOf(root).forEach(node => {{
                            const parent = node.parentElement;
                            const st = window.getComputedStyle(parent);
                            const weight = parseInt(st.fontWeight, 10) || 400;
                            const anchorEl = parent.closest && parent.closest('a[href]');
                            runs.push({{
                                text: (node.nodeValue || '').replace(/\s+/g, ' '),
                                bold: weight >= 600,
                                italic: st.fontStyle === 'italic' || st.fontStyle === 'oblique',
                                href: anchorEl ? anchorEl.getAttribute('href') : null
                            }});
                        }});
                        return runs;
                    }};

                    // T1-8: the float's text split by LINE, with each line's position
                    // relative to the element, so an invisible copy can be laid over the
                    // picture exactly where the words appear in it.
                    const positionedRunsOf = (root, bounds) => {{
                        const runs = [];
                        textNodesOf(root).forEach(node => {{
                            const st = window.getComputedStyle(node.parentElement);
                            const fontSizePt = (parseFloat(st.fontSize) || 12) * 0.75;
                            lineRunsOf(node).forEach(line => {{
                                const t = line.text.replace(/\s+/g, ' ').trim();
                                if (!t) return;
                                runs.push({{
                                    text: t,
                                    xPt: (line.rect.left - bounds.left) * 0.75,
                                    yPt: (line.rect.top - bounds.top) * 0.75,
                                    widthPt: line.rect.width * 0.75,
                                    heightPt: line.rect.height * 0.75,
                                    fontSizePt: fontSizePt
                                }});
                            }});
                        }});
                        return runs;
                    }};

                    // --- T1-5: footnotes -------------------------------------------
                    // `float: footnote` renders INLINE in Chromium — measured, the marker
                    // landed 16% into page 1 rather than at the page bottom — so the
                    // content is lifted out of the flow HERE and a call marker is left in
                    // its place. The footnote text itself is drawn into a reserved band at
                    // the bottom of whichever page the call lands on, and which page that
                    // is cannot be known until the document has been paginated. So this
                    // pass, like string-set above, only collects a text fingerprint.
                    const footnoteAssignments = [];
                    if (gcpm.footnotes && gcpm.footnotes.length) {{
                        const romanize = (n) => {{
                            const table = [[1000,'m'],[900,'cm'],[500,'d'],[400,'cd'],[100,'c'],[90,'xc'],
                                           [50,'l'],[40,'xl'],[10,'x'],[9,'ix'],[5,'v'],[4,'iv'],[1,'i']];
                            let out = '';
                            for (const [v, s] of table) {{ while (n >= v) {{ out += s; n -= v; }} }}
                            return out;
                        }};
                        const alphabetize = (n) => {{
                            let out = '';
                            while (n > 0) {{ const r = (n - 1) % 26; out = String.fromCharCode(97 + r) + out; n = Math.floor((n - 1) / 26); }}
                            return out;
                        }};
                        const formatCounter = (n, style) => {{
                            switch ((style || 'decimal').toLowerCase()) {{
                                case 'lower-roman': return romanize(n);
                                case 'upper-roman': return romanize(n).toUpperCase();
                                case 'lower-alpha': case 'lower-latin': return alphabetize(n);
                                case 'upper-alpha': case 'upper-latin': return alphabetize(n).toUpperCase();
                                default: return String(n);
                            }}
                        }};
                        // Evaluates a `content:` expression for ::footnote-call /
                        // ::footnote-marker. An expression we cannot evaluate degrades to
                        // the plain number rather than to an empty marker — an unmarked
                        // footnote is unreadable, a decimal one is merely unstyled.
                        const renderMarker = (expr, n) => {{
                            if (!expr) return String(n);
                            const re = /counter\s*\(\s*[\w-]+\s*(?:,\s*([\w-]+)\s*)?\)|'([^']*)'|""([^""]*)""/g;
                            let out = '', m, matched = false;
                            while ((m = re.exec(expr)) !== null) {{
                                matched = true;
                                if (m[0].toLowerCase().indexOf('counter') === 0) out += formatCounter(n, m[1]);
                                else out += (m[2] !== undefined ? m[2] : m[3]);
                            }}
                            return matched ? out : String(n);
                        }};

                        const els = [];
                        gcpm.footnotes.forEach(rule => {{
                            gcpmSelect(rule.selector).forEach(el => {{ if (els.indexOf(el) === -1) els.push(el); }});
                        }});
                        inDocumentOrder(els);

                        els.forEach((el) => {{
                            const text = norm(el.textContent);
                            if (!text) return; // an empty footnote has nothing to relocate

                            // Captured BEFORE the element is hidden. `display: none` does
                            // not inherit, so once the footnote is hidden its own direct
                            // text nodes are skipped while text inside a <b>, <i> or <a>
                            // survives — measured, the footnote came out reading
                            // 'Smith v. Jones2026 WL 1234the original filing', with every
                            // unstyled word between the emphasis silently missing.
                            const runs = styledRunsOf(el);

                            const number = footnoteAssignments.length + 1;
                            const callText = renderMarker(gcpm.footnoteCall && gcpm.footnoteCall.content, number);
                            const markerText = renderMarker(gcpm.footnoteMarker && gcpm.footnoteMarker.content, number);

                            // The call is a superscript number in the middle of body
                            // text, so it is NOT its own fingerprint — every page has
                            // digits on it. What locates it is the text around it.
                            const fp = surroundingFingerprint(el);

                            const computed = window.getComputedStyle(el);
                            const fontSizePt = (parseFloat(computed.fontSize) || 12) * 0.75;

                            const call = document.createElement('sup');
                            call.setAttribute('data-pdfengine-footnote-call', String(number));
                            call.textContent = callText;
                            call.style.cssText = 'vertical-align:super;line-height:0;'
                                + 'font-size:' + ((gcpm.footnoteCall && gcpm.footnoteCall.fontSizePt)
                                    ? (gcpm.footnoteCall.fontSizePt + 'pt') : '0.7em') + ';'
                                + ((gcpm.footnoteCall && gcpm.footnoteCall.color) ? ('color:' + gcpm.footnoteCall.color + ';') : '');
                            el.parentNode.insertBefore(call, el);

                            // Hidden, not removed: the element stays in the DOM so the
                            // author's own scripts and any later selector still see the
                            // document they wrote, and `display:none` is what actually
                            // takes it out of Chromium's layout and out of the PDF.
                            el.style.setProperty('display', 'none', 'important');
                            el.setAttribute('data-pdfengine-footnote', String(number));

                            footnoteAssignments.push({{
                                number: number,
                                text: text,
                                marker: markerText,
                                callMarker: callText,
                                fingerprint: fp.primary,
                                shortFingerprint: fp.shortFp,
                                fontSizePt: Math.min(14, Math.max(5, fontSizePt)),
                                // Any element inside the footnote is styling or a link the
                                // band cannot reproduce: it is drawn with PDF text
                                // operators in a single font. Counted so the loss can be
                                // REPORTED rather than discovered in the finished document.
                                hasInlineMarkup: el.children.length > 0,
                                runs: runs,
                                documentOrder: number - 1
                            }});
                        }});
                    }}

                    // --- T1-7: named pages -----------------------------------------
                    // `page: <name>` is silently ignored by Chromium — measured, a cover
                    // declared A4 landscape with 50mm margins came out identical to the
                    // body pages. Page geometry changes LAYOUT, so unlike a running header
                    // it cannot be corrected after the render: the section has to be
                    // rendered on its own paper and the parts stitched together.
                    //
                    // This pass only PARTITIONS. Consecutive top-level blocks sharing a
                    // page name form one run, which keeps the number of extra renders
                    // proportional to the number of geometry changes rather than to the
                    // number of sections.
                    const pageRuns = [];
                    if (gcpm.namedPageUses && gcpm.namedPageUses.length) {{
                        // A `page:` rule can match something nested deep inside a section.
                        // Page geometry applies to whole pages, so it is resolved up to
                        // the top-level block that actually owns the page — and widening
                        // like that is reported rather than done silently.
                        const nameOf = new Map();
                        let widened = 0;
                        gcpm.namedPageUses.forEach(rule => {{
                            gcpmSelect(rule.selector).forEach(el => {{
                                let node = el;
                                while (node.parentElement && node.parentElement !== document.body) {{
                                    node = node.parentElement;
                                }}
                                if (node.parentElement !== document.body) return;
                                if (node !== el) widened++;
                                if (!nameOf.has(node)) nameOf.set(node, rule.name);
                            }});
                        }});

                        const children = Array.from(document.body.children)
                            .filter(el => el.tagName !== 'SCRIPT' && el.tagName !== 'STYLE');
                        let current = null;
                        children.forEach(child => {{
                            const name = nameOf.get(child) || '';
                            if (!current || current.name !== name) {{
                                current = {{ name: name, index: pageRuns.length, elements: [] }};
                                pageRuns.push(current);
                            }}
                            current.elements.push(child);
                        }});

                        // A single run means every block shares one geometry, so there is
                        // nothing to split and nothing to stitch.
                        if (pageRuns.length > 1) {{
                            pageRuns.forEach(run => run.elements.forEach(
                                el => el.setAttribute('data-pdfengine-pagerun', String(run.index))));
                        }} else {{
                            pageRuns.length = 0;
                        }}
                        pageRuns.forEach(run => {{ delete run.elements; }});
                        if (widened > 0) pageRuns.widened = widened;
                    }}

                    // --- T1-8: page floats -----------------------------------------
                    // `float: top` / `float: bottom` pull a figure or table to a page
                    // edge. Measured: Chromium renders BOTH exactly where authored — the
                    // two edges produced identical output, 38% down page 1 — so, as with
                    // footnotes, the engine relocates the content.
                    //
                    // The element is only MARKED and measured here. It is deliberately
                    // left visible: the capture that replaces it has to be taken while the
                    // browser still has it laid out, and that capture is driven from the
                    // render service, which owns the screenshot API. Hiding happens there,
                    // immediately after.
                    const pageFloatAssignments = [];
                    if (gcpm.pageFloats && gcpm.pageFloats.length) {{
                        const floats = [];
                        gcpm.pageFloats.forEach(rule => {{
                            gcpmSelect(rule.selector).forEach(el => {{
                                if (floats.some(f => f.el === el)) return;
                                floats.push({{ el: el, edge: rule.edge }});
                            }});
                        }});
                        floats.sort((a, b) => {{
                            const pos = a.el.compareDocumentPosition(b.el);
                            if (pos & Node.DOCUMENT_POSITION_FOLLOWING) return -1;
                            if (pos & Node.DOCUMENT_POSITION_PRECEDING) return 1;
                            return 0;
                        }});

                        floats.forEach((entry) => {{
                            const el = entry.el;
                            const rect = el.getBoundingClientRect();
                            // A zero-sized element has nothing to place, and reserving a
                            // band of zero height for it would be a pure cost.
                            if (rect.width < 1 || rect.height < 1) return;

                            const number = pageFloatAssignments.length + 1;
                            const fp = surroundingFingerprint(el);

                            el.setAttribute('data-pdfengine-pagefloat', String(number));

                            pageFloatAssignments.push({{
                                number: number,
                                edge: entry.edge,
                                widthPt: rect.width * 0.75,
                                heightPt: rect.height * 0.75,
                                containsText: norm(el.textContent).length > 0,
                                textRuns: positionedRunsOf(el, rect),
                                fingerprint: fp.primary,
                                shortFingerprint: fp.shortFp,
                                documentOrder: number - 1
                            }});
                        }});
                    }}

                    // string-set: record the assignment plus a text fingerprint. The PAGE
                    // is deliberately NOT computed here — it is resolved against the real
                    // rendered PDF later, the same way cross-references are.
                    const stringSetAssignments = [];
                    gcpm.stringSets.forEach(rule => {{
                        gcpmSelect(rule.selector).forEach(el => {{
                            // readableText, not textContent: the fingerprint is compared
                            // against text read out of the RENDERED PDF, so it must reflect
                            // what is actually painted — element boundaries spaced, and
                            // anything already lifted out of the flow (a page float,
                            // redrawn later as an image) left out entirely.
                            const value = readableText(el);
                            if (!value) return;
                            let fingerprint = value;
                            let node = el, guard = 0;
                            while (fingerprint.length < 90 && guard++ < 25) {{
                                let next = node.nextElementSibling;
                                while (!next && node.parentElement && node.parentElement !== document.body) {{
                                    node = node.parentElement;
                                    next = node.nextElementSibling;
                                }}
                                if (!next) break;
                                fingerprint += ' ' + readableText(next);
                                node = next;
                            }}
                            stringSetAssignments.push({{
                                name: rule.name,
                                value: value,
                                fingerprint: fingerprint.replace(/\s+/g, ' ').trim().slice(0, 90),
                                shortFingerprint: value.slice(0, 80),
                                documentOrder: stringSetAssignments.length
                            }});
                        }});
                    }});

                    const pageRefRequests = [];
                    if ({((context.Options?.EnablePageReferences ?? true) ? "true" : "false")}) {{
                        const seenIds = new Set();
                        document.querySelectorAll('[data-pdfengine-pageref]').forEach(el => {{
                            const targetId = el.getAttribute('data-pdfengine-pageref');
                            if (!targetId || seenIds.has(targetId)) return;
                            seenIds.add(targetId);
                            const target = document.getElementById(targetId);
                            if (!target) {{
                                // A DANGLING reference — the id does not exist anywhere in
                                // the document. Recording it with an empty fingerprint is
                                // deliberate: returning early instead meant zero requests
                                // were produced, the whole resolution pass was skipped, and
                                // the entry rendered as a silent blank gap in the table of
                                // contents. A reference that cannot resolve must surface as
                                // '?' plus a diagnostic, never as nothing.
                                pageRefRequests.push({{ id: targetId, fingerprint: '', shortFingerprint: '' }});
                                return;
                            }}

                            // The anchor's own text is NOT a unique fingerprint: a table
                            // of contents repeats every section title verbatim, so
                            // searching the rendered PDF for the heading text alone
                            // matched the contents page itself and reported every
                            // section as landing on the ToC page. Extending the
                            // fingerprint forward into the content that FOLLOWS the
                            // anchor disambiguates it, because a ToC line contains only
                            // the title while the real section continues into its body.
                            let fingerprint = (target.textContent || '');
                            let node = target;
                            let guard = 0;
                            while (fingerprint.replace(/\s+/g, ' ').trim().length < 90 && guard++ < 25) {{
                                let next = node.nextElementSibling;
                                while (!next && node.parentElement && node.parentElement !== document.body) {{
                                    node = node.parentElement;
                                    next = node.nextElementSibling;
                                }}
                                if (!next) break;
                                fingerprint += ' ' + (next.textContent || '');
                                node = next;
                            }}
                            fingerprint = fingerprint.replace(/\s+/g, ' ').trim().slice(0, 90);

                            const shortFingerprint = (target.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 80);
                            if (fingerprint) pageRefRequests.push({{ id: targetId, fingerprint, shortFingerprint }});
                        }});
                    }}

                    return {{ headingOutline, pageWhitespace, pageRefRequests, listsWithMarkers, imagesWithoutAlt,
                              overflowingElements, widestOverflowPx, offPageElements, stringSetAssignments,
                              footnoteAssignments, pageFloatAssignments,
                              pageRuns: pageRuns.map(r => ({{ index: r.index, name: r.name }})),
                              unsatisfiableBlocks, widowOrphanRisks, measuredLineBoxes }};
                }})();
            ";
            var pass2Result = await page.EvaluateAsync<System.Text.Json.JsonElement>(paginationScript);

            if (pass2Result.TryGetProperty("headingOutline", out var headingOutlineJson) &&
                headingOutlineJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                plan.HeadingOutline.Clear();
                foreach (var entry in headingOutlineJson.EnumerateArray())
                {
                    var text = entry.GetProperty("text").GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    plan.HeadingOutline.Add(new HeadingOutlineEntry
                    {
                        Text = text,
                        Level = entry.GetProperty("level").GetInt32(),
                        Page = entry.GetProperty("page").GetInt32()
                    });
                }
            }

            if (pass2Result.TryGetProperty("listsWithMarkers", out var listsJson) && listsJson.GetInt32() > 0)
            {
                context.Diagnostics.Warnings.Add(
                    $"Accessibility warning: {listsJson.GetInt32()} list(s) use a visible bullet/number marker. Chromium emits list markers as untagged content, which fails PDF/UA-1 clause 7.1 (\"content shall be marked as Artifact or tagged as real content\"). Verified workaround: set 'list-style: none' on those lists. This is an upstream Chromium limitation — PdfEngine reports it rather than silently restyling your document.");
            }

            if (pass2Result.TryGetProperty("imagesWithoutAlt", out var altJson) && altJson.GetInt32() > 0)
            {
                context.Diagnostics.Warnings.Add(
                    $"Accessibility warning: {altJson.GetInt32()} image(s) have no 'alt' attribute. Each becomes a PDF Figure with no alternate text, failing PDF/UA-1 clause 7.3. Verified: adding alt text took an otherwise-identical document from 105/1 to 106/0 in veraPDF.");
            }

            // Gate A: content that will be cropped or is positioned off the page must be
            // surfaced. Silent loss here produces a PDF that looks complete and is not.
            if (pass2Result.TryGetProperty("overflowingElements", out var overflowJson) &&
                overflowJson.GetInt32() > 0)
            {
                var widest = pass2Result.TryGetProperty("widestOverflowPx", out var wJson)
                    ? wJson.GetInt32() : 0;
                context.Diagnostics.Warnings.Add(
                    $"Layout warning: {overflowJson.GetInt32()} element(s) OVERFLOW the printable width and will be clipped at the page edge (widest overhang: {widest}px). Content past the page boundary is not rendered in the PDF. Fix by constraining the width, allowing the content to wrap, or switching that section to landscape.");
            }

            if (pass2Result.TryGetProperty("offPageElements", out var offPageJson) &&
                offPageJson.GetInt32() > 0)
            {
                context.Diagnostics.Warnings.Add(
                    $"Layout warning: {offPageJson.GetInt32()} absolutely/fixed-positioned element(s) sit entirely OUTSIDE the page box and will not appear in the PDF. This is intentional for screen-reader-only text, but is also the usual symptom of a positioning bug — verify the content is meant to be invisible.");
            }

            if (pass2Result.TryGetProperty("unsatisfiableBlocks", out var unsatJson) &&
                unsatJson.GetInt32() > 0)
            {
                context.Diagnostics.Warnings.Add(
                    $"Typography warning: {unsatJson.GetInt32()} text block(s) are taller than a single page, so the requested orphans/widows of {orphans}/{widows} CANNOT be honoured for them — the browser must split them regardless. Consider breaking the content into shorter blocks.");
            }

            if (pass2Result.TryGetProperty("stringSetAssignments", out var stringSetJson) &&
                stringSetJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                plan.StringSetAssignments.Clear();
                foreach (var item in stringSetJson.EnumerateArray())
                {
                    plan.StringSetAssignments.Add(new StringSetAssignment
                    {
                        Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                        Value = item.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                        Fingerprint = item.TryGetProperty("fingerprint", out var f) ? f.GetString() ?? string.Empty : string.Empty,
                        ShortFingerprint = item.TryGetProperty("shortFingerprint", out var sf) ? sf.GetString() ?? string.Empty : string.Empty,
                        DocumentOrder = item.TryGetProperty("documentOrder", out var o) ? o.GetInt32() : 0,
                        Page = 0   // resolved against the real PDF by PlaywrightPdfService
                    });
                }
            }

            if (pass2Result.TryGetProperty("footnoteAssignments", out var footnoteJson) &&
                footnoteJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                plan.Footnotes.Clear();
                foreach (var item in footnoteJson.EnumerateArray())
                {
                    plan.Footnotes.Add(new FootnoteAssignment
                    {
                        Number = item.TryGetProperty("number", out var num) ? num.GetInt32() : 0,
                        Text = item.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                        Marker = item.TryGetProperty("marker", out var mk) ? mk.GetString() ?? string.Empty : string.Empty,
                        CallMarker = item.TryGetProperty("callMarker", out var cm) ? cm.GetString() ?? string.Empty : string.Empty,
                        Fingerprint = item.TryGetProperty("fingerprint", out var fp) ? fp.GetString() ?? string.Empty : string.Empty,
                        ShortFingerprint = item.TryGetProperty("shortFingerprint", out var sfp) ? sfp.GetString() ?? string.Empty : string.Empty,
                        FontSizePt = item.TryGetProperty("fontSizePt", out var fs) ? fs.GetDouble() : 9,
                        HasInlineMarkup = item.TryGetProperty("hasInlineMarkup", out var hm) && hm.GetBoolean(),
                        Runs = ReadFootnoteRuns(item),
                        DocumentOrder = item.TryGetProperty("documentOrder", out var ord) ? ord.GetInt32() : 0,
                        Page = 0   // resolved against the real PDF by PlaywrightPdfService
                    });
                }

                // The footnote area's font size, when the author declared one on
                // `@footnote`, overrides each footnote's own computed size — that is what
                // the declaration is for.
                if (plan.FootnoteArea.FontSizePt is > 0)
                {
                    foreach (var fn in plan.Footnotes) fn.FontSizePt = plan.FootnoteArea.FontSizePt!.Value;
                }

                // Stated per render, naming the count — the same rule the page-float
                // rasterization notice follows. A plain-text footnote loses nothing; one
                // carrying emphasis, a citation link or a nested reference loses it
                // silently otherwise.
                // Emphasis and links now survive into the band, so the notice fires only
                // for a footnote whose markup carried something the band still cannot
                // reproduce — a nested block, an image, a table. Reporting the cases that
                // ARE handled would be noise.
                // Links and bold now survive into the band; italic does not, because no
                // italic face is available to draw with (every bundled font file is a
                // Regular weight and PdfSharpCore has no resolver registered — measured,
                // XFontStyle.Italic renders identically to Regular). Reporting only what is
                // actually lost keeps the notice worth reading.
                // Emphasis now draws in a real bold or italic face. It is only reported
                // when the family the band was asked for is one the engine bundles
                // Regular-only, which is the one case it still cannot honour.
                var emphasised = plan.Footnotes.Count(f => f.Runs.Any(r => r.Bold || r.Italic));
                if (emphasised > 0 && !EngineFontResolver.SupportsEmphasis(plan.FootnoteArea.FontFamily))
                {
                    context.Diagnostics.Warnings.Add(
                        $"Footnote notice: {emphasised} footnote(s) use bold or italic, but the footnote band was asked for '{plan.FootnoteArea.FontFamily}', which the engine bundles in a Regular weight only — so that emphasis is drawn upright and regular. Remove the `@footnote {{ font-family }}` declaration to get the default face, which has a full bold and italic set.");
                }
            }

            if (pass2Result.TryGetProperty("pageRuns", out var runsJson) &&
                runsJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                plan.PageRuns.Clear();
                foreach (var item in runsJson.EnumerateArray())
                {
                    plan.PageRuns.Add(new NamedPageRun
                    {
                        Index = item.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0,
                        Name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? string.Empty : string.Empty
                    });
                }

                if (plan.PageRuns.Count > 1)
                {
                    context.Diagnostics.Warnings.Add(
                        $"Named page notice: this document declares {plan.PageRuns.Count} run(s) of content with differing page geometry, so it is rendered in {plan.PageRuns.Count} parts and stitched together — Chromium cannot vary page size or margins within one render. Each part costs one extra render.");
                }
            }

            if (pass2Result.TryGetProperty("pageFloatAssignments", out var floatJson) &&
                floatJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                plan.PageFloats.Clear();
                foreach (var item in floatJson.EnumerateArray())
                {
                    plan.PageFloats.Add(new PageFloatAssignment
                    {
                        Number = item.TryGetProperty("number", out var num) ? num.GetInt32() : 0,
                        Edge = item.TryGetProperty("edge", out var e) ? e.GetString() ?? "top" : "top",
                        WidthPt = item.TryGetProperty("widthPt", out var w) ? w.GetDouble() : 0,
                        HeightPt = item.TryGetProperty("heightPt", out var h) ? h.GetDouble() : 0,
                        ContainsText = item.TryGetProperty("containsText", out var ct) && ct.GetBoolean(),
                        TextRuns = ReadFloatTextRuns(item),
                        Fingerprint = item.TryGetProperty("fingerprint", out var fp) ? fp.GetString() ?? string.Empty : string.Empty,
                        ShortFingerprint = item.TryGetProperty("shortFingerprint", out var sfp) ? sfp.GetString() ?? string.Empty : string.Empty,
                        DocumentOrder = item.TryGetProperty("documentOrder", out var ord) ? ord.GetInt32() : 0,
                        Page = 0   // resolved against the real PDF by PlaywrightPdfService
                    });
                }

                // Stated once, per render, naming the count. A floated photograph loses
                // nothing by becoming an image; a floated table loses its text layer, and
                // that is not something a caller should have to discover by trying to
                // search the finished PDF.
                var withText = plan.PageFloats.Count(f => f.ContainsText);
                if (withText > 0)
                {
                    context.Diagnostics.Warnings.Add(
                        $"Page float notice: {withText} element(s) with `float: top`/`float: bottom` contain text and are drawn as IMAGES in the finished PDF, because arbitrary content cannot be redrawn from a text description the way a footnote can. Their text will not be selectable, searchable, or available to a screen reader. Chromium implements no page floats at all, so the alternative is leaving them mid-paragraph where they were authored.");
                }
            }

            if (pass2Result.TryGetProperty("pageRefRequests", out var pageRefJson) &&
                pageRefJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                plan.PageRefRequests.Clear();
                foreach (var entry in pageRefJson.EnumerateArray())
                {
                    plan.PageRefRequests.Add(new PageRefRequest
                    {
                        Id = entry.GetProperty("id").GetString() ?? string.Empty,
                        Fingerprint = entry.GetProperty("fingerprint").GetString() ?? string.Empty,
                        ShortFingerprint = entry.TryGetProperty("shortFingerprint", out var sf) ? (sf.GetString() ?? string.Empty) : string.Empty
                    });
                }
            }

            if (pass2Result.TryGetProperty("pageWhitespace", out var whitespaceJson) &&
                whitespaceJson.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in whitespaceJson.EnumerateArray())
                {
                    var wastedPx = entry.GetProperty("wastedPx").GetInt32();
                    var onPage = entry.GetProperty("page").GetInt32();
                    var reason = entry.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
                    var pctOfPage = printableHeightPx > 0 ? (wastedPx / printableHeightPx * 100.0) : 0;
                    context.Diagnostics.Warnings.Add(
                        $"Pagination Notice: Page {onPage} has ~{wastedPx}px ({pctOfPage:0}%) of trailing whitespace, caused by {reason ?? "a page break"}. If this section doesn't need its own page, consider removing the forced break; if it should always start fresh, this whitespace is expected.");
                }
            }

            return;
        }

        // Pass 1: Static Pre-flight (runs before page loading)
        if (string.IsNullOrEmpty(html))
        {
            return;
        }

        html = await ProtectRowspanContinuationsAsync(html);

        int headingCount = Regex.Matches(html, @"<h[1-6]\b", RegexOptions.IgnoreCase).Count;

        // Rough pre-render estimate only; the real page count comes from the rendered
        // PDF itself (GetPdfPageCount) once Pass 2 has actually run.
        plan.TotalEstimatedPages = headingCount > 0 ? (int)Math.Ceiling(headingCount / 4.0) : 1;

        var printStyleInject = @"
            @media print {
                /* No blanket break-before/after rule on headings here, deliberately.
                   Verified by testing: an `!important` stylesheet rule — even one that
                   says `auto`, meant only to cancel a (mistakenly assumed) implicit
                   default — beats a plain inline style, silently overriding every
                   `break-before: page` Pass 2's JS sets on a heading that genuinely
                   needs to move. Pass 2's inline styles must be the only thing
                   controlling heading breaks, with nothing in the stylesheet able to
                   outrank them.
                */
                .avoid-orphan {
                    page-break-inside: avoid !important;
                    break-inside: avoid !important;
                }
                /* Charts/diagrams are atomic — splitting a canvas or SVG mid-figure
                   would cut it visually in half, which is worse than moving it whole.
                   This is what makes the relaxed heading-orphan check above safe: large
                   trailing content that doesn't fit can now flow to the next page on its
                   own, cleanly, instead of being dragged down together with a heading
                   that would otherwise have fit fine where it was. */
                canvas, svg {
                    page-break-inside: avoid !important;
                    break-inside: avoid !important;
                }
                /* Native CSS fragmentation widow/orphan control. Measured: Chromium
                   implements both correctly — at orphans/widows 4 a paragraph that would
                   have split 5/3 was moved WHOLE to the next page — so the engine exposes
                   these rather than re-implementing line breaking. Previously hard-coded
                   to 2 with no way for a caller to change them. */
                p, li, td, th, blockquote {
                    orphans: __ORPHANS__;
                    widows: __WIDOWS__;
                }
            }
        ";

        // Substituted rather than interpolated: this CSS block is full of braces, and
        // switching the literal to $@"" would require escaping every one of them.
        printStyleInject = printStyleInject
            .Replace("__ORPHANS__", orphans.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("__WIDOWS__", widows.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var styleBlock = $"\n<style>{printStyleInject}</style>\n";

        var headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx != -1)
        {
            context.Html = html.Insert(headIdx + 6, styleBlock);
        }
        else
        {
            var htmlIdx = html.IndexOf("<html>", StringComparison.OrdinalIgnoreCase);
            if (htmlIdx != -1)
            {
                context.Html = html.Insert(htmlIdx + 6, $"\n<head>{styleBlock}</head>\n");
            }
            else
            {
                context.Html = styleBlock + html;
            }
        }

        if (headingCount > 15)
        {
            plan.PaginationWarnings.Add($"Pagination Warning: Document has {headingCount} headings. Complex page flows may result in pagination drift. Monitored keep-together overrides are active.");
        }

        foreach (var warning in plan.PaginationWarnings)
        {
            context.Diagnostics.Warnings.Add(warning);
        }
    }

    private static List<FootnoteRun> ReadFootnoteRuns(System.Text.Json.JsonElement item)
    {
        var runs = new List<FootnoteRun>();
        if (!item.TryGetProperty("runs", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return runs;

        foreach (var r in arr.EnumerateArray())
        {
            var text = r.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (text.Length == 0) continue;
            runs.Add(new FootnoteRun
            {
                Text = text,
                Bold = r.TryGetProperty("bold", out var b) && b.GetBoolean(),
                Italic = r.TryGetProperty("italic", out var i) && i.GetBoolean(),
                Href = r.TryGetProperty("href", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String
                    ? h.GetString() : null
            });
        }
        return runs;
    }

    private static List<PageFloatTextRun> ReadFloatTextRuns(System.Text.Json.JsonElement item)
    {
        var runs = new List<PageFloatTextRun>();
        if (!item.TryGetProperty("textRuns", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return runs;

        foreach (var r in arr.EnumerateArray())
        {
            var text = r.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (text.Length == 0) continue;
            runs.Add(new PageFloatTextRun
            {
                Text = text,
                XPt = r.TryGetProperty("xPt", out var x) ? x.GetDouble() : 0,
                YPt = r.TryGetProperty("yPt", out var y) ? y.GetDouble() : 0,
                WidthPt = r.TryGetProperty("widthPt", out var w) ? w.GetDouble() : 0,
                HeightPt = r.TryGetProperty("heightPt", out var hh) ? hh.GetDouble() : 0,
                FontSizePt = r.TryGetProperty("fontSizePt", out var fs) ? fs.GetDouble() : 9
            });
        }
        return runs;
    }

    /// <summary>
    /// Simulates the standard HTML table grid algorithm to find rows that are pure
    /// continuations of a rowspan opened by an earlier row, and marks them
    /// `break-before: avoid` so the browser's print engine won't land a page break
    /// between a spanning cell's opening row and its continuation rows — the gap this
    /// closes is the project's own open DEFECT-002 (rowspan cells cut across pages
    /// corrupt border alignment). This is a real parse via AngleSharp, not a regex
    /// guess at table structure, because column-position tracking through colspan/
    /// rowspan needs an actual grid simulation to get right.
    /// </summary>
    internal static async Task<string> ProtectRowspanContinuationsAsync(string html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("rowspan", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        using var document = await context.OpenAsync(req => req.Content(html));

        foreach (var table in document.QuerySelectorAll("table"))
        {
            var rows = table.QuerySelectorAll("tr").ToList();
            var occupancy = new Dictionary<int, int>(); // column index -> remaining rows the active span still covers after this one

            foreach (var row in rows)
            {
                var isContinuation = occupancy.Values.Any(remaining => remaining > 0);
                if (isContinuation)
                {
                    var existingStyle = row.GetAttribute("style") ?? string.Empty;
                    if (!existingStyle.Contains("break-before", StringComparison.OrdinalIgnoreCase))
                    {
                        row.SetAttribute("style", $"{existingStyle};break-before:avoid;page-break-before:avoid;".TrimStart(';'));
                    }
                }

                foreach (var col in occupancy.Keys.ToList())
                {
                    occupancy[col]--;
                    if (occupancy[col] <= 0) occupancy.Remove(col);
                }

                var colCursor = 0;
                foreach (var cell in row.Children.Where(c => c.TagName is "TD" or "TH"))
                {
                    while (occupancy.ContainsKey(colCursor)) colCursor++;

                    var colspan = int.TryParse(cell.GetAttribute("colspan"), out var cs) ? Math.Max(1, cs) : 1;
                    var rowspan = int.TryParse(cell.GetAttribute("rowspan"), out var rs) ? Math.Max(1, rs) : 1;

                    if (rowspan > 1)
                    {
                        for (var c = colCursor; c < colCursor + colspan; c++)
                        {
                            occupancy[c] = rowspan - 1;
                        }
                    }

                    colCursor += colspan;
                }
            }
        }

        // AngleSharp's DocumentElement.OuterHtml doesn't include the doctype declaration
        // — reattach it so downstream stages still see standards mode, matching what
        // HtmlSanitizerStage already guarantees earlier in the pipeline.
        return "<!DOCTYPE html>\n" + document.DocumentElement.OuterHtml;
    }

    /// <summary>
    /// Derives the actual printable page height in CSS px from the effective page size,
    /// orientation, and margins — replacing what used to be a hardcoded 900px constant
    /// that ignored the document's real page geometry entirely.
    /// </summary>
    internal static double ComputePrintableHeightPx(RenderingOptions? options)
    {
        var pageSize = options?.PageSize ?? "A4";
        if (!PageSizesPx.TryGetValue(pageSize, out var dims))
        {
            dims = PageSizesPx["A4"];
        }

        var heightPx = (options?.Landscape ?? false) ? dims.WidthPx : dims.HeightPx;

        var marginTop = ParseCssSizeToPx(options?.MarginTop);
        var marginBottom = ParseCssSizeToPx(options?.MarginBottom);

        var printable = heightPx - marginTop - marginBottom;

        // Guard against pathological/near-zero margins collapsing the usable area.
        return printable > 50 ? printable : heightPx;
    }

    /// <summary>
    /// Companion to <see cref="ComputePrintableHeightPx"/> — the printable content
    /// width, needed to make Pass 2's screen-mode measurement viewport match the
    /// actual print content width. Without this, text wraps differently at
    /// measurement time (default screen viewport) than at actual PDF-generation time
    /// (narrower print content area), so every height measured in Pass 2 can be
    /// systematically wrong — verified by testing to be the deeper cause behind
    /// several of the pagination inconsistencies found in this session.
    /// </summary>
    internal static double ComputePrintableWidthPx(RenderingOptions? options)
    {
        var pageSize = options?.PageSize ?? "A4";
        if (!PageSizesPx.TryGetValue(pageSize, out var dims))
        {
            dims = PageSizesPx["A4"];
        }

        var widthPx = (options?.Landscape ?? false) ? dims.HeightPx : dims.WidthPx;

        var marginLeft = ParseCssSizeToPx(options?.MarginLeft);
        var marginRight = ParseCssSizeToPx(options?.MarginRight);

        var printable = widthPx - marginLeft - marginRight;

        return printable > 50 ? printable : widthPx;
    }

    /// <summary>
    /// Full page width in px (no margin subtracted) — needed for FullHeight mode,
    /// where Width/Height replace Format entirely and margins are applied by
    /// Playwright separately on top, the same way they apply on top of Format.
    /// </summary>
    internal static double ComputePageWidthPx(RenderingOptions? options)
    {
        var pageSize = options?.PageSize ?? "A4";
        if (!PageSizesPx.TryGetValue(pageSize, out var dims))
        {
            dims = PageSizesPx["A4"];
        }

        return (options?.Landscape ?? false) ? dims.HeightPx : dims.WidthPx;
    }

    /// <summary>
    /// Converts a CSS length (px, in, cm, mm, pt, pc) to px at 96 DPI. em/rem/vh/vw/%
    /// are viewport- or font-relative and can't be resolved without live layout, so
    /// they fall back to their raw numeric value as a best-effort approximation.
    /// </summary>
    internal static double ParseCssSizeToPx(string? cssSize)
    {
        if (string.IsNullOrWhiteSpace(cssSize)) return 0;

        var match = Regex.Match(cssSize.Trim(), @"^(\d+(?:\.\d+)?)(px|in|cm|mm|pt|pc|em|rem|vh|vw|%)?$", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;

        var value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "px";

        return unit switch
        {
            "px" => value,
            "in" => value * 96,
            "cm" => value * 37.7953,
            "mm" => value * 3.77953,
            "pt" => value * (96.0 / 72.0),
            "pc" => value * 16,
            "em" or "rem" => value * 16,
            _ => value
        };
    }
}
