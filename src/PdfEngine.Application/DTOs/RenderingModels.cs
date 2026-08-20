using System.Collections.Generic;

namespace PdfEngine.Application.DTOs;

public class DocumentModel
{
    public int NodeCount { get; set; }
    public int MaxDepth { get; set; }
    public bool HasTemplatePlaceholders { get; set; }
    public bool HasUnclosedTags { get; set; }
    public List<string> ParserWarnings { get; set; } = new();
}

public class LayoutModel
{
    public List<string> OverflowElements { get; set; } = new();
    public List<string> OverlappingElements { get; set; } = new();
    public double EstimatedPageUtilization { get; set; }
    public string LayoutRiskScore { get; set; } = "Low";
    public List<string> LayoutWarnings { get; set; } = new();
}

public class PaginationPlan
{
    public List<int> PageBreaks { get; set; } = new();
    public int TotalEstimatedPages { get; set; }
    public List<string> OrphanedHeadings { get; set; } = new();
    public List<string> PaginationWarnings { get; set; } = new();

    // Populated by Pass 2 of PaginationPlanner using the same page-tracking state that
    // decides actual breaks, so the PDF outline/bookmarks stay consistent with where
    // content really landed rather than a second, independently-computed estimate.
    public List<HeadingOutlineEntry> HeadingOutline { get; set; } = new();

    // Cross-reference targets awaiting resolution. Real page numbers are not knowable
    // from the DOM (see PaginationPlanner) — they are resolved against the actually
    // rendered PDF and substituted in a second render pass.
    public List<PageRefRequest> PageRefRequests { get; set; } = new();

    // GCPM running headers/footers (T1-1). Each entry is one `string-set` assignment
    // found in the document, in document order, with the page it landed on. Chromium
    // supports none of this — its headerTemplate is a single fixed template for the whole
    // document — so the values are collected here and stamped per page in the same
    // post-process pass that draws watermarks.
    public List<StringSetAssignment> StringSetAssignments { get; set; } = new();

    // The `@page` margin boxes (@top-left, @bottom-center, ...) declared by the document,
    // with their content expressions still unevaluated.
    public List<MarginBoxRequest> MarginBoxes { get; set; } = new();

    // GCPM footnotes (T1-5). Each entry is one `float: footnote` element that has been
    // lifted out of the text flow and replaced in place by a call marker. Chromium
    // supports none of this — measured 2026-08-18, `float: footnote` content renders
    // INLINE exactly where it was authored — so the page each call landed on is resolved
    // from the real rendered PDF and the content is drawn into a reserved band at the
    // bottom of that page.
    public List<FootnoteAssignment> Footnotes { get; set; } = new();

    // The `@page { @footnote { ... } }` area style. Always present so the stamping pass
    // has a complete set of values without null checks; defaults match the conventional
    // typographic footnote rule.
    public FootnoteAreaRequest FootnoteArea { get; set; } = new();

    // GCPM page floats (T1-8). Each entry is one `float: top` / `float: bottom` element,
    // lifted out of the flow by the same machinery as footnotes and re-drawn into a band
    // reserved at the top or bottom of the page its original position landed on. Measured
    // 2026-08-18: Chromium renders both edges INLINE where authored, indistinguishable
    // from no float at all.
    public List<PageFloatAssignment> PageFloats { get; set; } = new();

    // GCPM named pages (T1-7). The `@page <name>` geometries the document declares, and
    // the runs of top-level content bound to them. Measured 2026-08-18: Chromium silently
    // ignores `page: <name>` — a cover page declared A4 landscape with 50mm margins came
    // out identical to the body pages — and this CANNOT be corrected after the fact,
    // because page geometry changes layout rather than just what is stamped on top of it.
    // Each run is therefore rendered separately and the parts are stitched together.
    public Dictionary<string, NamedPageDefinition> NamedPages { get; set; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    public List<NamedPageRun> PageRuns { get; set; } = new();

    // The plain `@page { }` geometry, and which of `:first`/`:left`/`:right` declare
    // geometry of their own. Both exist for one reason: a stitched document renders each
    // run separately, and Chromium treats the first page of EVERY part as `:first`, so an
    // author's `@page :first` has to be actively cancelled on every part but the first.
    public NamedPageDefinition? DefaultPage { get; set; }
    public List<string> PseudoPagesWithGeometry { get; set; } = new();

    // Where the top band's upper edge sits, in PDF points down from the sheet's top edge.
    // Companion to FootnoteBandBaseYPt for `float: top`.
    public double FloatBandBaseTopPt { get; set; }

    // Where the footnote band's bottom edge sits, in PDF points up from the sheet edge —
    // normally the caller's bottom margin, measured from the first render when the caller
    // left the margin to the document's own `@page` rule. Fixed by the reflow pass and
    // reused by the stamping pass so the two cannot disagree about where the band goes.
    public double FootnoteBandBaseYPt { get; set; }
}

/// <summary>
/// One `float: footnote` element after the planner has lifted it out of the text flow.
///
/// The <see cref="Page"/> is NOT computed from DOM geometry. A footnote must appear on
/// the page holding its call, and which page that is only exists once the document has
/// actually been paginated — the same reason cross-references and running headers read
/// the rendered PDF instead of the DOM.
/// </summary>
public class FootnoteAssignment
{
    /// <summary>1-based, continuous through the document.</summary>
    public int Number { get; set; }

    /// <summary>The footnote's text, flattened. Inline markup inside a footnote is not
    /// preserved: the band is drawn with PDF text operators, exactly like running
    /// headers, so it carries one font and no inline styling.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Marker drawn at the start of the footnote body, e.g. "1" or "*".</summary>
    public string Marker { get; set; } = string.Empty;

    /// <summary>Marker drawn inline at the call site. Usually identical to
    /// <see cref="Marker"/>, but `::footnote-call` and `::footnote-marker` can differ.</summary>
    public string CallMarker { get; set; } = string.Empty;

    public int DocumentOrder { get; set; }

    /// <summary>Text immediately surrounding the call site, used to find which rendered
    /// page the call landed on.</summary>
    public string Fingerprint { get; set; } = string.Empty;
    public string ShortFingerprint { get; set; } = string.Empty;

    /// <summary>Resolved against the REAL rendered PDF. 0 means unresolved.</summary>
    public int Page { get; set; }

    /// <summary>The footnote element's own computed font size, in points.</summary>
    public double FontSizePt { get; set; } = 9;

    /// <summary>True when the footnote contains elements — emphasis, a link, a nested
    /// reference. Drives the diagnostic, but only for markup the band still cannot
    /// reproduce; a plain-text footnote loses nothing and says nothing.</summary>
    public bool HasInlineMarkup { get; set; }

    /// <summary>
    /// The footnote's text broken into styled runs, in reading order.
    ///
    /// Emphasis and links survive into the drawn band because each run is drawn with its
    /// own font and a link run also becomes a real PDF annotation. <see cref="Text"/> stays
    /// the flattened form, and is what gets drawn if no runs were captured.
    /// </summary>
    public List<FootnoteRun> Runs { get; set; } = new();
}

/// <summary>One stretch of footnote text sharing a single style.</summary>
public class FootnoteRun
{
    public string Text { get; set; } = string.Empty;
    public bool Bold { get; set; }
    public bool Italic { get; set; }

    /// <summary>Set when this run sits inside a link; null otherwise.</summary>
    public string? Href { get; set; }
}

/// <summary>
/// The page geometry declared by one `@page &lt;name&gt;` rule (T1-7).
///
/// Only what forces a separate render is recorded: paper size, orientation and margins.
/// Everything else an author writes in that block is ordinary CSS and reaches Chromium
/// unchanged.
/// </summary>
public class NamedPageDefinition
{
    public string Name { get; set; } = string.Empty;

    /// <summary>A named paper size, e.g. "A4" or "Letter". Null when the rule declared an
    /// explicit width/height pair, or no size at all.</summary>
    public string? PageSize { get; set; }

    /// <summary>Explicit `size: 210mm 148mm` pair, as authored.</summary>
    public string? Width { get; set; }
    public string? Height { get; set; }

    /// <summary>Null means the rule said nothing about orientation.</summary>
    public bool? Landscape { get; set; }

    public string? MarginTop { get; set; }
    public string? MarginRight { get; set; }
    public string? MarginBottom { get; set; }
    public string? MarginLeft { get; set; }

    /// <summary>True when this rule changes page geometry at all. A named page that only
    /// restyles a margin box needs no separate render.</summary>
    public bool ChangesGeometry =>
        PageSize != null || Width != null || Landscape != null
        || MarginTop != null || MarginRight != null
        || MarginBottom != null || MarginLeft != null;
}

/// <summary>
/// A run of consecutive top-level content that shares one page name (T1-7). An empty
/// <see cref="Name"/> is the document's default page.
/// </summary>
public class NamedPageRun
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>How many pages this run produced. Filled in after it is rendered, and used
    /// to map a run-local page number onto the stitched document.</summary>
    public int PageCount { get; set; }
}

/// <summary>
/// One `float: top` / `float: bottom` element after the planner has lifted it out of the
/// text flow (T1-8).
///
/// Unlike a footnote, a page float is arbitrary content — a figure, a chart, a table — so
/// it cannot be redrawn from text. It is captured as an image while it is still laid out
/// by the browser, and that image is drawn into the reserved band. The cost of that is
/// real and is reported: the floated content leaves no text layer behind it.
/// </summary>
public class PageFloatAssignment
{
    public int Number { get; set; }

    /// <summary>"top" or "bottom" — which page edge the content is pulled to.</summary>
    public string Edge { get; set; } = "top";

    /// <summary>PNG of the element as the browser laid it out, base64-encoded.</summary>
    public string ImageBase64 { get; set; } = string.Empty;

    /// <summary>
    /// The author's own description of the float, from `aria-label`, `alt` or `title`.
    ///
    /// A page float is drawn as pixels, so this is the ONLY thing assistive technology
    /// gets — there is no text layer underneath it to fall back on. Empty means the author
    /// gave no description, which is reported rather than papered over with a generic
    /// label that tells a screen-reader user nothing.
    /// </summary>
    public string? AltText { get; set; }

    /// <summary>The element's laid-out size, in points. The image is drawn to fit this
    /// box, so a capture taken at a higher pixel density simply prints sharper.</summary>
    public double WidthPt { get; set; }
    public double HeightPt { get; set; }

    /// <summary>True when the element carries real text, which the capture turns into
    /// pixels. Drives the diagnostic — a floated photograph loses nothing, a floated
    /// table loses its text layer, and the caller should be told which one happened.</summary>
    public bool ContainsText { get; set; }

    public int DocumentOrder { get; set; }

    /// <summary>Text around the float's authored position, used to find which rendered
    /// page it belongs on — the same mechanism as footnote calls.</summary>
    public string Fingerprint { get; set; } = string.Empty;
    public string ShortFingerprint { get; set; } = string.Empty;

    /// <summary>Resolved against the REAL rendered PDF. 0 means unresolved.</summary>
    public int Page { get; set; }

    /// <summary>
    /// The float's own text with the position of each line, measured relative to the
    /// element's top-left corner in points.
    ///
    /// The float is drawn as an image, and images carry no text. These runs are re-drawn
    /// over the image as INVISIBLE text at the same coordinates, which is how a scanned
    /// document carries an OCR layer: the picture is what you see, the text layer is what
    /// you select, search, and what a screen reader reads.
    /// </summary>
    public List<PageFloatTextRun> TextRuns { get; set; } = new();
}

/// <summary>One line of text inside a page float, positioned relative to its top-left.</summary>
public class PageFloatTextRun
{
    public string Text { get; set; } = string.Empty;
    public double XPt { get; set; }
    public double YPt { get; set; }
    public double WidthPt { get; set; }
    public double HeightPt { get; set; }
    public double FontSizePt { get; set; } = 9;
}

/// <summary>
/// The `@page { @footnote { ... } }` area: the rule above the footnotes and the type
/// used to draw them.
/// </summary>
public class FootnoteAreaRequest
{
    public bool SeparatorEnabled { get; set; } = true;

    /// <summary>Separator length as a fraction of the content width. The conventional
    /// footnote rule is a short rule, not a full-width one.</summary>
    public double SeparatorWidthFraction { get; set; } = 0.3;

    /// <summary>Absolute separator length in points; overrides the fraction when > 0.</summary>
    public double SeparatorWidthPt { get; set; }

    public double SeparatorThicknessPt { get; set; } = 0.5;
    public string SeparatorColor { get; set; } = "#000000";

    /// <summary>Blank space between the last line of body text and the separator.</summary>
    public double SpaceAbovePt { get; set; } = 8;

    /// <summary>Blank space between the separator and the first footnote line.</summary>
    public double SpaceBelowPt { get; set; } = 4;

    /// <summary>Vertical gap between consecutive footnotes on the same page.</summary>
    public double ItemGapPt { get; set; } = 2;

    /// <summary>Set only when `@footnote { font-size }` is declared; otherwise each
    /// footnote uses its own computed size.</summary>
    public double? FontSizePt { get; set; }

    public string FontFamily { get; set; } = "Helvetica";
    public string Color { get; set; } = "#000000";
}

/// <summary>One `string-set: name content()` assignment and where it landed.</summary>
public class StringSetAssignment
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int Page { get; set; }
    public int DocumentOrder { get; set; }

    // Resolved against the REAL rendered PDF using the same fingerprint mechanism as
    // cross-references, not from DOM geometry. A running header naming the wrong chapter
    // is exactly as wrong as a table of contents naming the wrong page, and DOM-geometry
    // estimation was already proven wrong twice for cross-references.
    public string Fingerprint { get; set; } = string.Empty;
    public string ShortFingerprint { get; set; } = string.Empty;
}

/// <summary>
/// A `@page` margin box awaiting evaluation, e.g. `@top-center { content: string(chapter) }`.
/// </summary>
public class MarginBoxRequest
{
    /// <summary>e.g. "top-center", "bottom-right".</summary>
    public string Box { get; set; } = string.Empty;

    /// <summary>The raw `content:` expression, evaluated per page at stamp time.</summary>
    public string Content { get; set; } = string.Empty;

    public string FontFamily { get; set; } = "Helvetica";
    public double FontSize { get; set; } = 9;
    public string Color { get; set; } = "#000000";

    /// <summary>Page selector restriction: null (all), "first", "left", "right".</summary>
    public string? PageSelector { get; set; }
}

public class PageRefRequest
{
    public string Id { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public string ShortFingerprint { get; set; } = string.Empty;
}

public class HeadingOutlineEntry
{
    public string Text { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Page { get; set; }
}


/// <summary>
/// One file to embed in the PDF (T2-1).
///
/// The relationship matters as much as the bytes: a Factur-X reader looks for an attachment
/// whose <see cref="Relationship"/> is <c>Data</c> and ignores anything else, so getting it
/// wrong produces a file that opens fine and is invisible to the system meant to read it.
/// </summary>
public class PdfAttachment
{
    /// <summary>File name as it appears in the reader's attachment pane.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The file's bytes, base64-encoded.</summary>
    public string ContentBase64 { get; set; } = string.Empty;

    /// <summary>MIME type, e.g. "text/xml" or "application/pdf".</summary>
    public string MimeType { get; set; } = "application/octet-stream";

    /// <summary>Shown beside the attachment in the reader.</summary>
    public string? Description { get; set; }

    /// <summary>PDF/A-3 associated-file relationship: Data, Source, Alternative,
    /// Supplement or Unspecified. Factur-X and ZUGFeRD require <c>Data</c>.</summary>
    public string Relationship { get; set; } = "Data";
}


/// <summary>
/// An interactive form field to place on the rendered page (T2-3).
///
/// Position is in POINTS from the top-left of the page, matching how the rest of the API
/// talks about a page, rather than PDF's bottom-left origin — the conversion is the
/// engine's problem, not the caller's.
/// </summary>
public class PdfFormField
{
    /// <summary>Field name. This is the key the filled value comes back under, so it has
    /// to be unique within the document.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>text | checkbox</summary>
    public string Type { get; set; } = "text";

    /// <summary>Pre-filled value. For a checkbox, "true"/"on" ticks it.</summary>
    public string? Value { get; set; }

    /// <summary>1-based page to place the field on.</summary>
    public int Page { get; set; } = 1;

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 160;
    public double Height { get; set; } = 18;

    public double FontSize { get; set; } = 10;
    public bool ReadOnly { get; set; }
    public bool Required { get; set; }

    /// <summary>Shown by a reader as a tooltip.</summary>
    public string? ToolTip { get; set; }
}
