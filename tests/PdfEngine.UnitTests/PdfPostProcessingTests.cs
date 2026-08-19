using System.IO;
using System.Linq;
using PdfEngine.Application.DTOs;
using PdfEngine.Infrastructure.Services;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Xunit;

namespace PdfEngine.UnitTests;

public class PdfPostProcessingTests
{
    private static byte[] CreateMultiPagePdf(int pageCount)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < pageCount; i++)
        {
            var page = document.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i + 1}", new XFont("Helvetica", 20), XBrushes.Black, new XPoint(50, 50));
        }
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ApplyPdfPostProcessing_GeneratesNestedOutlineFromHeadings()
    {
        var original = CreateMultiPagePdf(3);
        var headings = new System.Collections.Generic.List<HeadingOutlineEntry>
        {
            new() { Text = "Chapter 1", Level = 1, Page = 1 },
            new() { Text = "Section 1.1", Level = 2, Page = 2 },
            new() { Text = "Chapter 2", Level = 1, Page = 3 },
        };

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, new RenderingOptions { GenerateOutlineFromHeadings = true }, headings);

        using var reopened = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.ReadOnly);
        Assert.Equal(2, reopened.Outlines.Count); // Chapter 1, Chapter 2 at top level
        Assert.Equal("Chapter 1", reopened.Outlines[0].Title);
        Assert.Single(reopened.Outlines[0].Outlines); // Section 1.1 nested under Chapter 1
        Assert.Equal("Section 1.1", reopened.Outlines[0].Outlines[0].Title);
        Assert.Equal("Chapter 2", reopened.Outlines[1].Title);
    }

    [Fact]
    public void ApplyPdfPostProcessing_NoHeadings_ProducesNoOutline()
    {
        var original = CreateMultiPagePdf(1);

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, new RenderingOptions { GenerateOutlineFromHeadings = true }, headingOutline: null);

        using var reopened = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.ReadOnly);
        Assert.Empty(reopened.Outlines);
    }

    /// <summary>
    /// Asserts on the produced bytes rather than on a reopened document object:
    /// PDFsharp reports SecuritySettings.IsEncrypted against the decrypted in-memory
    /// state, so it is False after a successful open and proves nothing. The /Encrypt
    /// dictionary and the AESV3 crypt filter are the real evidence of AES-256 (RB-1).
    /// </summary>
    private static void AssertUsesAes256(byte[] pdfBytes)
    {
        var raw = System.Text.Encoding.Latin1.GetString(pdfBytes);
        Assert.Contains("/Encrypt", raw);
        Assert.Contains("AESV3", raw);   // AESV3 == AES-256 (Standard V5 R6)
        Assert.DoesNotContain("/V 2", raw); // would indicate legacy RC4-128
    }

    [Fact]
    public void ApplyPdfPostProcessing_Watermark_AppearsInRenderedText()
    {
        var original = CreateMultiPagePdf(1);

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, new RenderingOptions { WatermarkText = "CONFIDENTIAL DRAFT" }, headingOutline: null);

        using var document = UglyToad.PdfPig.PdfDocument.Open(result);
        var allText = string.Concat(document.GetPages().SelectMany(p => p.Letters).Select(l => l.Value));
        Assert.Contains("CONFIDENTIAL", allText);
    }

    [Fact]
    public void ApplyPdfPostProcessing_Encryption_RejectsWrongPasswordAcceptsRight()
    {
        var original = CreateMultiPagePdf(2);
        var options = new RenderingOptions
        {
            OwnerPassword = "owner-secret",
            UserPassword = "user-secret",
            AllowPrinting = false
        };

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, options, headingOutline: null);

        // Read back with PDFsharp: output is now AES-256 (Standard V5 R6), which
        // PdfSharpCore's reader cannot open at all — that is the point of RB-1.
        Assert.ThrowsAny<Exception>(() =>
            PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(result), "wrong-password",
                PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly));

        using var reopened = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(result), "user-secret",
            PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly);
        Assert.Equal(2, reopened.PageCount);
        AssertUsesAes256(result);
    }

    [Fact]
    public void ApplyPdfPostProcessing_OutlinePlusEncryption_NowKeepsOutlineInsteadOfSkippingIt()
    {
        // REPLACES an earlier test that asserted the outline was SKIPPED when combined
        // with encryption. That workaround existed because PdfSharpCore garbled outline
        // titles while encrypting. Encryption now runs as a separate AES-256 pass after
        // PdfSharpCore has finished, so the conflict is gone and the outline must
        // actually survive. The old behaviour is now the bug.
        var original = CreateMultiPagePdf(1);
        var headings = new System.Collections.Generic.List<HeadingOutlineEntry>
        {
            new() { Text = "Chapter 1", Level = 1, Page = 1 }
        };
        var options = new RenderingOptions { GenerateOutlineFromHeadings = true, UserPassword = "secret" };

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, options, headings);

        using var reopened = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(result), "secret",
            PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly);
        AssertUsesAes256(result);
        Assert.NotEmpty(reopened.Outlines);
        Assert.Equal("Chapter 1", reopened.Outlines[0].Title);
    }

    [Fact]
    public void ApplyPdfPostProcessing_MetadataPlusEncryption_NowPreservesMetadata()
    {
        // Also replaces a workaround: PdfSharpCore corrupted /Info strings when it
        // encrypted, so metadata used to be skipped entirely. It must now survive.
        var original = CreateMultiPagePdf(1);
        var options = new RenderingOptions
        {
            Title = "Encrypted Title",
            Author = "PdfEngine",
            UserPassword = "secret"
        };

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, options, headingOutline: null);

        using var reopened = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(result), "secret",
            PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly);
        Assert.Equal("Encrypted Title", reopened.Info.Title);
        Assert.Equal("PdfEngine", reopened.Info.Author);
    }

    [Fact]
    public void ApplyPdfPostProcessing_NothingRequested_ReturnsOriginalBytesUnchanged()
    {
        var original = CreateMultiPagePdf(1);

        var result = PlaywrightPdfService.ApplyPdfPostProcessing(original, new RenderingOptions { GenerateOutlineFromHeadings = false }, headingOutline: null);

        Assert.Same(original, result);
    }
}
