using System.Collections.Generic;
using System.Linq;
using PdfEngine.Application.DTOs;
using PdfEngine.Infrastructure.Services;
using PdfSharpCore.Drawing;
using Xunit;

namespace PdfEngine.UnitTests;

/// <summary>
/// Browser-free tests for the footnote band's geometry and styling (backlog T1-5).
///
/// The one thing that must not drift is that the space RESERVED for a footnote band and
/// the space the drawing pass actually CONSUMES are computed from the same layout and the
/// same font metrics. Every point of disagreement is either whitespace the document did
/// not need to lose, or footnote text drawn on top of body text — and the gate cannot see
/// the difference between "fits" and "fits by luck".
/// </summary>
public class FootnoteLayoutTests
{
    private static XGraphics Measure() => XGraphics.CreateMeasureContext(
        new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards);

    private static FootnoteAssignment Footnote(string text, string marker = "1", double sizePt = 9) =>
        new() { Number = 1, Text = text, Marker = marker, CallMarker = marker, FontSizePt = sizePt, Page = 1 };

    private static List<string> LineTexts(List<List<PlaywrightPdfService.PlacedToken>> lines) =>
        lines.Select(line => string.Join(" ", line.Select(t => t.Token.Text))).ToList();

    private static List<List<PlaywrightPdfService.PlacedToken>> Layout(
        XGraphics gfx, FootnoteAssignment footnote, double width) =>
        PlaywrightPdfService.LayoutFootnoteTokens(
            gfx, PlaywrightPdfService.TokenizeFootnote(footnote), "Helvetica", footnote.FontSizePt, width);

    [Fact]
    public void WrappedLinesNeverExceedTheAvailableWidth()
    {
        using var gfx = Measure();
        const double width = 180;

        var lines = Layout(gfx, Footnote(
            "See the consolidated statement of financial position and the accompanying "
            + "notes, which form an integral part of these financial statements."), width);

        Assert.True(lines.Count > 1, "the fixture must actually wrap");
        Assert.All(lines, line =>
        {
            if (line.Count == 0) return;
            var last = line[^1];
            Assert.True(last.XPt + last.WidthPt <= width + 0.5,
                $"line overruns: ends at {last.XPt + last.WidthPt:F1} of {width}");
        });
    }

    [Fact]
    public void AWordLongerThanTheLineIsBrokenRatherThanOverflowing()
    {
        // A long URL in a citation is the ordinary case; a script with no spaces is the
        // same problem. Either way an unbroken token must not silently run off the page.
        using var gfx = Measure();
        const double width = 60;

        var lines = Layout(gfx, Footnote(
            "https://example.invalid/reports/2026/consolidated-financial-statements.pdf"), width);

        Assert.True(lines.Count > 1);
        Assert.All(lines, line =>
        {
            if (line.Count == 0) return;
            var last = line[^1];
            Assert.True(last.XPt + last.WidthPt <= width + 0.5);
        });
    }

    [Fact]
    public void WrappingLosesNoText()
    {
        using var gfx = Measure();
        const string text = "Adjusted for the disposal of the logistics segment in Q3.";

        var joined = string.Concat(LineTexts(Layout(gfx, Footnote(text), 120)));

        Assert.Equal(text.Replace(" ", string.Empty), joined.Replace(" ", string.Empty));
    }

    [Fact]
    public void BandHeightMatchesWhatDrawingConsumes()
    {
        // Reproduces the drawing pass's own vertical arithmetic and asserts the reserved
        // height covers exactly it. If these two ever diverge the band overlaps body text,
        // and no fixture would necessarily catch it.
        using var gfx = Measure();
        var area = new FootnoteAreaRequest();
        var footnotes = new List<FootnoteAssignment>
        {
            Footnote("A short note.", "1"),
            Footnote("A considerably longer note that has to wrap across more than one line "
                     + "to be laid out at this width at all.", "2"),
        };
        const double contentWidth = 400;

        var reserved = PlaywrightPdfService.ComputeFootnoteBandHeightPt(gfx, footnotes, area, contentWidth);

        var consumed = area.SpaceAbovePt + area.SeparatorThicknessPt + area.SpaceBelowPt;
        foreach (var footnote in footnotes)
        {
            var font = PlaywrightPdfService.ResolveFootnoteFont(area.FontFamily, footnote.FontSizePt);
            var indent = gfx.MeasureString(footnote.Marker + " ", font).Width;
            var lines = PlaywrightPdfService.LayoutFootnoteTokens(
                gfx, PlaywrightPdfService.TokenizeFootnote(footnote),
                area.FontFamily, footnote.FontSizePt, contentWidth - indent);
            consumed += lines.Count * font.GetHeight() + area.ItemGapPt;
        }

        Assert.Equal(consumed, reserved, 3);
    }

    // --- styled runs (the T1-5 limitation closed) ---------------------------------

    [Fact]
    public void StyledRunsSurviveIntoTheLayout()
    {
        // Previously the band was drawn in one font and every run's styling was flattened.
        using var gfx = Measure();
        var footnote = Footnote("placeholder");
        footnote.Runs = new List<FootnoteRun>
        {
            new() { Text = "See " },
            new() { Text = "Smith v. Jones", Italic = true },
            new() { Text = " and the ", },
            new() { Text = "filing", Href = "https://example.invalid/filing" },
        };

        var tokens = PlaywrightPdfService.TokenizeFootnote(footnote);

        Assert.Contains(tokens, t => t.Text == "Smith" && t.Italic);
        Assert.Contains(tokens, t => t.Text == "filing" && t.Href == "https://example.invalid/filing");
        Assert.Contains(tokens, t => t.Text == "See" && !t.Italic && t.Href == null);
    }

    [Fact]
    public void EveryRunStyleResolvesToAUsableFont()
    {
        // Measured, and worth pinning: PdfSharpCore has no font resolver registered here
        // and every bundled font file is a Regular weight, so Bold and Italic currently
        // resolve to the SAME face — which is why bold is double-struck at draw time and
        // italics are reported rather than claimed. What must hold regardless is that each
        // style resolves to something drawable, so the day a real resolver is registered
        // this upgrades on its own instead of throwing.
        foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var font = PlaywrightPdfService.ResolveFootnoteFont("Helvetica", 9, bold, italic);
            Assert.NotNull(font);
            Assert.True(font.Size > 0);
        }
    }

    [Fact]
    public void LayoutMeasuresEachRunWithItsOwnFont()
    {
        // The reserved height and the drawn height are computed from this same layout, so
        // a run measured in the wrong font would silently put them out of step.
        using var gfx = Measure();
        const string word = "consolidated";
        var footnote = Footnote("placeholder");
        footnote.Runs = new List<FootnoteRun> { new() { Text = word, Bold = true } };

        var placed = Layout(gfx, footnote, 400)[0][0];
        var expected = gfx.MeasureString(word, PlaywrightPdfService.ResolveFootnoteFont("Helvetica", 9, bold: true));

        Assert.Equal(expected.Width, placed.WidthPt, 2);
        Assert.True(placed.Token.Bold);
    }

    [Fact]
    public void AFootnoteWithNoCapturedRunsFallsBackToItsFlatText()
    {
        using var gfx = Measure();
        var footnote = Footnote("Plain text with no runs captured at all.");

        Assert.Empty(footnote.Runs);
        Assert.Contains("Plain", string.Concat(LineTexts(Layout(gfx, footnote, 300))));
    }

    // --- font resolution ----------------------------------------------------------

    [Fact]
    public void AnUnknownTypefaceFallsBackInsteadOfFailingTheRender()
    {
        var font = PlaywrightPdfService.ResolveFootnoteFont("NoSuchTypefaceExists-Regular", 9);

        Assert.NotNull(font);
        Assert.True(font.Size > 0);
    }

    [Fact]
    public void FootnoteFontSizeIsClampedToALegibleRange()
    {
        Assert.Equal(5, PlaywrightPdfService.ResolveFootnoteFont("Helvetica", 1).Size);
        Assert.Equal(14, PlaywrightPdfService.ResolveFootnoteFont("Helvetica", 40).Size);
    }

    [Fact]
    public void MoreFootnotesReserveMoreSpace()
    {
        using var gfx = Measure();
        var area = new FootnoteAreaRequest();

        var one = PlaywrightPdfService.ComputeFootnoteBandHeightPt(
            gfx, new List<FootnoteAssignment> { Footnote("Note.", "1") }, area, 400);
        var two = PlaywrightPdfService.ComputeFootnoteBandHeightPt(
            gfx, new List<FootnoteAssignment> { Footnote("Note.", "1"), Footnote("Note.", "2") }, area, 400);

        Assert.True(two > one, $"two footnotes reserved {two}pt, one reserved {one}pt");
    }

    [Fact]
    public void SuppressingTheSeparatorReducesTheReservedHeight()
    {
        using var gfx = Measure();
        var footnotes = new List<FootnoteAssignment> { Footnote("Note.", "1") };

        var withRule = PlaywrightPdfService.ComputeFootnoteBandHeightPt(
            gfx, footnotes, new FootnoteAreaRequest { SeparatorEnabled = true }, 400);
        var withoutRule = PlaywrightPdfService.ComputeFootnoteBandHeightPt(
            gfx, footnotes, new FootnoteAreaRequest { SeparatorEnabled = false }, 400);

        Assert.True(withoutRule < withRule);
    }
}
