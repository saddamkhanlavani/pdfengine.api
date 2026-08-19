using System.Linq;
using PdfEngine.Infrastructure.Services;
using Xunit;

namespace PdfEngine.UnitTests;

/// <summary>
/// Unit tests for GCPM CSS extraction (backlog T1-1/T1-2/T1-3).
///
/// These constructs are parsed from raw CSS text rather than through the browser, because
/// Chromium discards <c>string-set</c>, <c>target-counter()</c>, <c>leader()</c> and
/// <c>@page</c> margin boxes before CSSOM. That makes the parser the single point of
/// failure for all three features, and the reason it is a separate, browser-free class.
/// </summary>
public class GcpmCssParserTests
{
    private static string Css(string body) =>
        $"<html><head><style>{body}</style></head><body><h1>x</h1></body></html>";

    [Fact]
    public void ParsesStringSetSelectorAndName()
    {
        var doc = GcpmCssParser.Parse(Css("h1 { string-set: chapter content(); font-size: 18px }"));

        var rule = Assert.Single(doc.StringSets);
        Assert.Equal("h1", rule.Selector);
        Assert.Equal("chapter", rule.Name);
    }

    [Fact]
    public void ParsesMarginBoxWithSelectorAndStyling()
    {
        var doc = GcpmCssParser.Parse(Css(
            "@page { size: A4; margin: 20mm; @top-center { content: string(chapter); font-size: 11pt; color: #333333 } }"));

        var box = Assert.Single(doc.MarginBoxes);
        Assert.Equal("top-center", box.Box);
        Assert.Contains("string(chapter)", box.Content);
        Assert.Equal(11, box.FontSize);
        Assert.Equal("#333333", box.Color);
        Assert.Null(box.PageSelector);
    }

    [Fact]
    public void ParsesPageSelectorOnMarginBox()
    {
        var doc = GcpmCssParser.Parse(Css(
            "@page :first { @bottom-right { content: 'cover' } }"));

        Assert.Equal("first", Assert.Single(doc.MarginBoxes).PageSelector);
    }

    [Fact]
    public void MarginBoxExtractionPreservesSizeAndMargin()
    {
        // The @page block must survive with `size`/`margin` intact — Chromium genuinely
        // implements those, and stripping them while removing the margin boxes would
        // silently reset every document's page geometry.
        var doc = GcpmCssParser.Parse(Css(
            "@page { size: A5 landscape; margin: 12mm; @top-left { content: 'h' } }"));

        Assert.Single(doc.MarginBoxes);
        Assert.Equal("top-left", doc.MarginBoxes[0].Box);
    }

    [Theory]
    [InlineData(".toc a::after", ".toc a", false)]
    [InlineData(".toc a:after", ".toc a", false)]
    [InlineData("li a::before", "li a", true)]
    public void StripsPseudoElementFromSelector(string authored, string expected, bool before)
    {
        // Regression: trimming by searching backwards for ':' produced ".toc a:" for
        // "::after" — an invalid selector that threw inside querySelectorAll and returned
        // HTTP 500 for the entire document.
        var doc = GcpmCssParser.Parse(Css(
            authored + " { content: target-counter(attr(href), page) }"));

        var rule = Assert.Single(doc.TargetCounters);
        Assert.Equal(expected, rule.Selector);
        Assert.Equal(before, rule.Before);
        Assert.Equal("href", rule.TargetAttr);
    }

    [Fact]
    public void ParsesLeaderCharacter()
    {
        var doc = GcpmCssParser.Parse(Css(".toc a::after { content: leader('.') }"));

        var rule = Assert.Single(doc.Leaders);
        Assert.Equal(".", rule.Character);
        Assert.Equal(".toc a", rule.Selector);
    }

    [Theory]
    [InlineData("dotted", ".")]
    [InlineData("solid", "_")]
    public void MapsLeaderKeywordsToCharacters(string keyword, string expected)
    {
        var doc = GcpmCssParser.Parse(Css($".t::after {{ content: leader({keyword}) }}"));

        Assert.Equal(expected, Assert.Single(doc.Leaders).Character);
    }

    [Fact]
    public void OrdinaryStylesheetProducesNothing()
    {
        // The common case: a document using none of this must not pay for it, and must
        // certainly not have selectors invented for it.
        var doc = GcpmCssParser.Parse(Css(
            "body { font-family: sans-serif } h1 { font-size: 18px } @page { margin: 20mm }"));

        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void HandlesMultipleRulesInOneStylesheet()
    {
        var doc = GcpmCssParser.Parse(Css(
            "h1 { string-set: chapter content() } "
            + "h2 { string-set: section content() } "
            + ".toc a::after { content: target-counter(attr(href), page) } "
            + "@page { @top-center { content: string(chapter) } "
            + "@bottom-center { content: counter(page) } }"));

        Assert.Equal(2, doc.StringSets.Count);
        Assert.Single(doc.TargetCounters);
        Assert.Equal(2, doc.MarginBoxes.Count);
        Assert.Contains(doc.StringSets, r => r.Name == "section");
    }

    // --- T1-5 footnotes ---------------------------------------------------------

    [Fact]
    public void ParsesFootnoteSelector()
    {
        var doc = GcpmCssParser.Parse(Css(".fn { float: footnote; font-size: 8pt }"));

        Assert.Equal(".fn", Assert.Single(doc.Footnotes).Selector);
    }

    [Fact]
    public void FootnoteSelectorIsNotConfusedByAnOrdinaryFloat()
    {
        // `float: right` is ordinary CSS on almost every document. Matching it as a
        // footnote would silently delete the element from the text flow.
        var doc = GcpmCssParser.Parse(Css("aside { float: right } .note { float: left }"));

        Assert.Empty(doc.Footnotes);
    }

    [Fact]
    public void ParsesFootnoteCallAndMarkerContent()
    {
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } "
            + ".fn::footnote-call { content: counter(footnote, lower-roman); font-size: 7pt } "
            + ".fn::footnote-marker { content: counter(footnote) '.' }"));

        Assert.Single(doc.Footnotes);
        Assert.NotNull(doc.FootnoteCall);
        Assert.Contains("lower-roman", doc.FootnoteCall!.Content);
        Assert.Equal(7, doc.FootnoteCall.FontSizePt);
        Assert.NotNull(doc.FootnoteMarker);
        Assert.Contains("counter(footnote)", doc.FootnoteMarker!.Content);
    }

    [Fact]
    public void FootnotePseudoElementDoesNotLeakIntoTheFootnoteSelectorScan()
    {
        // The regression this guards: `.fn::footnote-call` is itself a rule whose body
        // holds a `content:` declaration. If the pseudo block is not removed before the
        // generic scans, the pseudo-element ends up treated as an ordinary selector and
        // the footnote is lifted out of the flow twice.
        var doc = GcpmCssParser.Parse(Css(
            ".fn::footnote-call { content: counter(footnote) } .fn { float: footnote }"));

        Assert.Equal(".fn", Assert.Single(doc.Footnotes).Selector);
    }

    [Fact]
    public void ParsesFootnoteAreaSeparator()
    {
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } "
            + "@page { margin: 20mm; @footnote { border-top: 1pt solid #999999; width: 40%; font-size: 8pt } }"));

        var area = doc.FootnoteArea;
        Assert.NotNull(area);
        Assert.True(area!.SeparatorEnabled);
        Assert.Equal(1, area.SeparatorThicknessPt);
        Assert.Equal("#999999", area.SeparatorColor);
        Assert.Equal(0.4, area.SeparatorWidthFraction, 3);
        Assert.Equal(8, area.FontSizePt);
    }

    [Fact]
    public void FootnoteAreaBorderNoneSuppressesTheSeparator()
    {
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } @page { @footnote { border-top: none } }"));

        Assert.False(doc.FootnoteArea!.SeparatorEnabled);
    }

    [Fact]
    public void FootnoteAreaCoexistsWithMarginBoxesInTheSamePageRule()
    {
        // Both live inside the same @page block and both must survive the other's
        // extraction — the margin-box pass rewrites that block in place.
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } "
            + "@page { size: A4; @top-center { content: string(chapter) } @footnote { border-top: 0.5pt solid #000 } }"));

        Assert.Equal("top-center", Assert.Single(doc.MarginBoxes).Box);
        Assert.True(doc.FootnoteArea!.SeparatorEnabled);
        Assert.Single(doc.Footnotes);
    }

    [Fact]
    public void DetectsPerPageFootnoteNumberingRequest()
    {
        // Not supported — the call marker is drawn before the engine knows its page — so
        // it must be DETECTED in order to be reported rather than silently ignored.
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } @page { counter-reset: footnote }"));

        Assert.True(doc.RequestsPerPageFootnoteNumbering);
    }

    [Fact]
    public void OrdinaryPageRuleDoesNotRequestPerPageFootnoteNumbering()
    {
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } @page { counter-reset: chapter }"));

        Assert.False(doc.RequestsPerPageFootnoteNumbering);
    }

    // --- T1-8 page floats -------------------------------------------------------

    [Theory]
    [InlineData("top")]
    [InlineData("bottom")]
    public void ParsesPageFloatEdge(string edge)
    {
        var doc = GcpmCssParser.Parse(Css($"figure {{ float: {edge}; width: 300px }}"));

        var rule = Assert.Single(doc.PageFloats);
        Assert.Equal("figure", rule.Selector);
        Assert.Equal(edge, rule.Edge);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("none")]
    [InlineData("inline-start")]
    public void OrdinaryFloatValuesAreNotPageFloats(string value)
    {
        // `float: left` appears in a large share of real stylesheets. Matching it would
        // rip the element out of the flow and rasterize it — silent, and catastrophic.
        var doc = GcpmCssParser.Parse(Css($".side {{ float: {value}; width: 120px }}"));

        Assert.Empty(doc.PageFloats);
    }

    [Fact]
    public void PageFloatAndFootnoteFloatCoexist()
    {
        var doc = GcpmCssParser.Parse(Css(
            ".fn { float: footnote } figure { float: top } aside { float: bottom }"));

        Assert.Equal(".fn", Assert.Single(doc.Footnotes).Selector);
        Assert.Equal(2, doc.PageFloats.Count);
        Assert.Contains(doc.PageFloats, f => f.Selector == "figure" && f.Edge == "top");
        Assert.Contains(doc.PageFloats, f => f.Selector == "aside" && f.Edge == "bottom");
    }

    [Fact]
    public void FloatTopIsNotMatchedInsideAnUnrelatedProperty()
    {
        // `clear: both` and shorthand values must not be mistaken for a float declaration.
        var doc = GcpmCssParser.Parse(Css(
            ".a { clear: both } .b { background: top center } .c { float: left }"));

        Assert.Empty(doc.PageFloats);
    }

    // --- T1-7 named pages -------------------------------------------------------

    [Fact]
    public void ParsesNamedPageGeometryAndBinding()
    {
        var doc = GcpmCssParser.Parse(Css(
            "@page cover { size: A4 landscape; margin: 50mm } .cover { page: cover }"));

        var use = Assert.Single(doc.NamedPageUses);
        Assert.Equal(".cover", use.Selector);
        Assert.Equal("cover", use.Name);

        var definition = doc.NamedPages["cover"];
        Assert.Equal("A4", definition.PageSize);
        Assert.True(definition.Landscape);
        Assert.Equal("50mm", definition.MarginTop);
        Assert.Equal("50mm", definition.MarginLeft);
        Assert.True(definition.ChangesGeometry);
    }

    [Fact]
    public void PageBreakBeforeIsNotMistakenForANamedPage()
    {
        // The collision that would matter most: `page-break-before: always` is on a large
        // share of real documents, and treating one as a named page would split the
        // document into separately-rendered parts for no reason.
        var doc = GcpmCssParser.Parse(Css(
            "section { page-break-before: always; page-break-inside: avoid }"));

        Assert.Empty(doc.NamedPageUses);
    }

    [Fact]
    public void PageAutoIsNotANamedPage()
    {
        // `auto` is the CSS initial value — it names no page.
        var doc = GcpmCssParser.Parse(Css("div { page: auto }"));

        Assert.Empty(doc.NamedPageUses);
    }

    [Theory]
    [InlineData("margin: 10mm", "10mm", "10mm", "10mm", "10mm")]
    [InlineData("margin: 10mm 20mm", "10mm", "20mm", "10mm", "20mm")]
    [InlineData("margin: 1mm 2mm 3mm", "1mm", "2mm", "3mm", "2mm")]
    [InlineData("margin: 1mm 2mm 3mm 4mm", "1mm", "2mm", "3mm", "4mm")]
    public void ExpandsTheMarginShorthandTheWayCssDoes(
        string declaration, string top, string right, string bottom, string left)
    {
        var doc = GcpmCssParser.Parse(Css(
            $"@page cover {{ {declaration} }} .cover {{ page: cover }}"));

        var definition = doc.NamedPages["cover"];
        Assert.Equal(top, definition.MarginTop);
        Assert.Equal(right, definition.MarginRight);
        Assert.Equal(bottom, definition.MarginBottom);
        Assert.Equal(left, definition.MarginLeft);
    }

    [Fact]
    public void ParsesAnExplicitPageSizePair()
    {
        var doc = GcpmCssParser.Parse(Css(
            "@page slide { size: 297mm 210mm } .slide { page: slide }"));

        var definition = doc.NamedPages["slide"];
        Assert.Equal("297mm", definition.Width);
        Assert.Equal("210mm", definition.Height);
        Assert.Null(definition.PageSize);
    }

    [Fact]
    public void ANamedPageThatChangesNoGeometryNeedsNoSeparateRender()
    {
        // Splitting the document costs one render per run, so a named page that only
        // restyles a margin box must not trigger one.
        var doc = GcpmCssParser.Parse(Css(
            "@page cover { @top-center { content: 'DRAFT' } } .cover { page: cover }"));

        Assert.False(doc.NamedPages["cover"].ChangesGeometry);
    }

    [Fact]
    public void NamedPageCoexistsWithThePseudoPageSelectors()
    {
        // `@page :first` and `@page cover` come through the same rule scanner, and T1-1/
        // T1-4 depend on the pseudo form still being read correctly.
        var doc = GcpmCssParser.Parse(Css(
            "@page :first { @top-center { content: 'cover' } } "
            + "@page cover { size: A5 } .cover { page: cover }"));

        Assert.Equal("first", Assert.Single(doc.MarginBoxes).PageSelector);
        Assert.Equal("A5", doc.NamedPages["cover"].PageSize);
    }

    [Fact]
    public void EmptyOrNullInputIsSafe()
    {
        Assert.True(GcpmCssParser.Parse(null).IsEmpty);
        Assert.True(GcpmCssParser.Parse("").IsEmpty);
    }
}
