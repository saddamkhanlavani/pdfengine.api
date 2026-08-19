using System.IO;
using System.Text;
using PdfEngine.Infrastructure.Services;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Xunit;

namespace PdfEngine.UnitTests;

/// <summary>
/// Regression tests for RB-2 — logical-order /ActualText on right-to-left runs.
///
/// The shipped defect: Chromium writes an RTL run in visual order inside
/// `/ReversedChars BMC`, and extractors reverse it at CHARACTER level. A ligature
/// glyph whose /ToUnicode value is TWO characters (lam-alef -> U+0644 U+0627) gets
/// split and emitted backwards, so "الاتجاه" extracted as "االتجاه" — the document
/// renders perfectly while search and copy/paste silently fail.
///
/// These tests build the PDF structure by hand rather than rendering, so they pin the
/// exact transformation without needing a browser.
/// </summary>
public class ActualTextRtlTests
{
    // Mirrors a real Chromium subset font: single-char glyphs plus one ligature glyph
    // (00DA) whose value is two characters.
    private const string ToUnicodeCMap = @"/CIDInit /ProcSet findresource begin
12 dict begin begincmap
1 begincodespacerange
<0000> <FFFF>
endcodespacerange
4 beginbfchar
<0154> <0644>
<0164> <0648>
<00DA> <06440627>
<00F0> <0627>
endbfchar
endcmap
end end";

    /// <summary>Builds a one-page PDF whose content stream is a /ReversedChars run.</summary>
    private static byte[] BuildPdfWithReversedRun(string glyphSequence)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();

        var toUnicode = new PdfDictionary(document);
        toUnicode.CreateStream(Encoding.Latin1.GetBytes(ToUnicodeCMap));
        document.Internals.AddObject(toUnicode);

        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type0");
        font.Elements.SetReference("/ToUnicode", toUnicode);
        document.Internals.AddObject(font);

        var fontResources = new PdfDictionary(document);
        fontResources.Elements.SetReference("/F4", font);
        var resources = new PdfDictionary(document);
        resources.Elements.SetObject("/Font", fontResources);
        page.Elements.SetObject("/Resources", resources);

        var content = new PdfDictionary(document);
        content.CreateStream(Encoding.Latin1.GetBytes(
            $"BT\n/ReversedChars BMC\n/F4 18 Tf\n{glyphSequence}EMC\nET\n"));
        document.Internals.AddObject(content);
        page.Elements.SetReference("/Contents", content);

        using var buffer = new MemoryStream();
        document.Save(buffer);
        return buffer.ToArray();
    }

    private static string ContentStreamOf(byte[] pdfBytes)
    {
        using var input = new MemoryStream(pdfBytes);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var contents = document.Pages[0].Contents;
        var sb = new StringBuilder();
        for (var i = 0; i < contents.Elements.Count; i++)
            sb.Append(Encoding.Latin1.GetString(
                contents.Elements.GetDictionary(i).Stream.UnfilteredValue));
        return sb.ToString();
    }

    [Fact]
    public void LigatureRun_GetsActualTextInLogicalOrder()
    {
        // Visual order: lam, waw, [lam+alef], alef.
        // Correct logical order reverses the GLYPHS, not the characters inside one:
        //   alef, [lam+alef], waw, lam = U+0627 U+0644 U+0627 U+0648 U+0644 = "الاول"
        var pdf = BuildPdfWithReversedRun(
            "<0154> Tj\n<0164> Tj\n<00DA> Tj\n<00F0> Tj\n");

        var stream = ContentStreamOf(PlaywrightPdfService.ApplyActualTextToReversedRuns(pdf));

        Assert.Contains("/ActualText <FEFF06270644062706480644>", stream);
        Assert.Contains("BDC", stream);
        // The /ReversedChars marker must be REPLACED, not nested: extractors apply
        // /ActualText AND the reversal, which double-reverses correct text.
        Assert.DoesNotContain("/ReversedChars", stream);
    }

    [Fact]
    public void RunWithoutMultiCharGlyph_IsLeftCompletelyAlone()
    {
        // Every glyph maps 1:1, so character-level reversal is already correct and
        // intervening measurably regressed real output. Scope must stay narrow.
        var pdf = BuildPdfWithReversedRun("<0154> Tj\n<0164> Tj\n<00F0> Tj\n");

        var result = PlaywrightPdfService.ApplyActualTextToReversedRuns(pdf);

        Assert.Equal(pdf, result);                                  // byte-identical: no re-save
        Assert.DoesNotContain("/ActualText", ContentStreamOf(result));
        Assert.Contains("/ReversedChars", ContentStreamOf(result));
    }

    [Fact]
    public void UnmappedGlyph_AbandonsTheRunRatherThanEmittingPartialText()
    {
        // 0999 is not in the CMap. Partially decoded text looks plausible but is missing
        // characters, which is worse than leaving Chromium's own bytes in place.
        var pdf = BuildPdfWithReversedRun("<0154> Tj\n<00DA> Tj\n<0999> Tj\n");

        var result = PlaywrightPdfService.ApplyActualTextToReversedRuns(pdf);

        Assert.Equal(pdf, result);
        Assert.DoesNotContain("/ActualText", ContentStreamOf(result));
    }

    [Fact]
    public void DocumentWithNoReversedRun_IsReturnedByteIdentical()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var buffer = new MemoryStream();
        document.Save(buffer);
        var original = buffer.ToArray();

        Assert.Equal(original, PlaywrightPdfService.ApplyActualTextToReversedRuns(original));
    }

    [Fact]
    public void GlyphsShownViaTjArray_AreDecodedToo()
    {
        // Chromium also emits [ <..> <..> ] TJ with kerning numbers interleaved.
        var pdf = BuildPdfWithReversedRun("[<0154> -12 <00DA> 5 <00F0>] TJ\n");

        var stream = ContentStreamOf(PlaywrightPdfService.ApplyActualTextToReversedRuns(pdf));

        // reversed glyphs: alef, [lam+alef], lam
        Assert.Contains("/ActualText <FEFF0627064406270644>", stream);
    }
}
