using System.IO;
using PdfEngine.Application.DTOs;
using PdfEngine.Infrastructure.Services;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Xunit;

namespace PdfEngine.UnitTests;

public class PdfMetadataTests
{
    private static byte[] CreateMinimalPdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var stream = new MemoryStream();
        document.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ApplyPdfMetadata_WritesRealInfoDictionary()
    {
        var original = CreateMinimalPdf();
        var options = new RenderingOptions
        {
            Title = "Q3 Financial Report",
            Author = "PdfEngine Test Suite",
            Subject = "Quarterly Results",
            Keywords = "finance, report, q3"
        };

        var result = PlaywrightPdfService.ApplyPdfMetadata(original, options);

        using var reopened = PdfReader.Open(new MemoryStream(result), PdfDocumentOpenMode.ReadOnly);
        Assert.Equal("Q3 Financial Report", reopened.Info.Title);
        Assert.Equal("PdfEngine Test Suite", reopened.Info.Author);
        Assert.Equal("Quarterly Results", reopened.Info.Subject);
        Assert.Equal("finance, report, q3", reopened.Info.Keywords);
    }

    [Fact]
    public void ApplyPdfMetadata_NoMetadataRequested_ReturnsOriginalBytesUnchanged()
    {
        var original = CreateMinimalPdf();

        var result = PlaywrightPdfService.ApplyPdfMetadata(original, new RenderingOptions());

        Assert.Same(original, result);
    }
}
