using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Infrastructure.Services;
using Xunit;

namespace PdfEngine.UnitTests;

public class DomAnalyzerTests
{
    private static async Task<RenderingContext> AnalyzeAsync(string html)
    {
        var analyzer = new DomAnalyzer();
        var context = new RenderingContext(html, new GeneratePdfDiagnostics(), CancellationToken.None);
        await analyzer.ExecuteAsync(context);
        return context;
    }

    [Fact]
    public async Task ExecuteAsync_VoidElementsWithoutSelfClosingSlash_AreNotFlaggedAsUnclosed()
    {
        var html = "<html><body><img src=\"a.png\"><br><input type=\"text\"><hr><meta charset=\"utf-8\"></body></html>";

        var context = await AnalyzeAsync(html);

        Assert.False(context.Model.HasUnclosedTags);
    }

    [Fact]
    public async Task ExecuteAsync_SelfClosingVoidElements_AreNotFlaggedAsUnclosed()
    {
        var html = "<html><body><img src=\"a.png\" /><br /></body></html>";

        var context = await AnalyzeAsync(html);

        Assert.False(context.Model.HasUnclosedTags);
    }

    [Fact]
    public async Task ExecuteAsync_GenuinelyUnclosedTag_IsStillFlagged()
    {
        var html = "<html><body><div><p>Unclosed paragraph and div</body></html>";

        var context = await AnalyzeAsync(html);

        Assert.True(context.Model.HasUnclosedTags);
    }

    [Fact]
    public async Task ExecuteAsync_WellFormedDocument_ReportsNoUnclosedTags()
    {
        var html = "<html><body><div><p>Fine.</p></div></body></html>";

        var context = await AnalyzeAsync(html);

        Assert.False(context.Model.HasUnclosedTags);
    }
}
