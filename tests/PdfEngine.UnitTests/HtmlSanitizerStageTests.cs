using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Infrastructure.Services;
using Xunit;

namespace PdfEngine.UnitTests;

public class HtmlSanitizerStageTests
{
    private static async Task<RenderingContext> SanitizeAsync(string html, bool allowScripts = false)
    {
        var stage = new HtmlSanitizerStage(NullLogger<HtmlSanitizerStage>.Instance);
        var context = new RenderingContext(html, new GeneratePdfDiagnostics(), CancellationToken.None)
        {
            Options = new RenderingOptions { AllowScripts = allowScripts }
        };
        await stage.ExecuteAsync(context);
        return context;
    }

    [Fact]
    public async Task ExecuteAsync_StripsScriptTags()
    {
        var context = await SanitizeAsync("<html><body><script>alert('xss')</script><p>Hello</p></body></html>");

        Assert.DoesNotContain("<script", context.Html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello", context.Html);
    }

    [Fact]
    public async Task ExecuteAsync_StripsInlineEventHandlers()
    {
        var context = await SanitizeAsync("<html><body><img src=\"a.png\" onerror=\"alert(1)\"></body></html>");

        Assert.DoesNotContain("onerror", context.Html, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_StripsJavascriptUris()
    {
        var context = await SanitizeAsync("<html><body><a href=\"javascript:alert(1)\">click</a></body></html>");

        Assert.DoesNotContain("javascript:", context.Html, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesSafeMarkupAndStyles()
    {
        var context = await SanitizeAsync(
            "<html><head><style>.title { color: red; }</style></head><body><div class=\"title\">Invoice #1</div></body></html>");

        Assert.Contains("Invoice #1", context.Html);
        Assert.Contains("<style>", context.Html);
        // The CSS sanitizer re-serializes color values (e.g. "red" -> "rgba(255, 0, 0, 1)")
        // rather than dropping the rule — the property must survive, exact form may differ.
        Assert.Contains("color:", context.Html);
        Assert.Contains("class=\"title\"", context.Html);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesInlineSvgGraphics()
    {
        var context = await SanitizeAsync(
            "<html><body><svg width=\"10\" height=\"10\"><circle cx=\"5\" cy=\"5\" r=\"4\" fill=\"red\"/></svg></body></html>");

        Assert.Contains("<svg", context.Html);
        Assert.Contains("<circle", context.Html);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesDataImageUris()
    {
        const string dataUri = "data:image/png;base64,iVBORw0KGgo=";
        var context = await SanitizeAsync($"<html><body><img src=\"{dataUri}\"></body></html>");

        Assert.Contains(dataUri, context.Html);
    }

    [Fact]
    public async Task ExecuteAsync_NullOptions_DefaultsToStrictScriptStripping()
    {
        var stage = new HtmlSanitizerStage(NullLogger<HtmlSanitizerStage>.Instance);
        var context = new RenderingContext(
            "<html><body><script>new Chart()</script><p>Hello</p></body></html>",
            new GeneratePdfDiagnostics(), CancellationToken.None); // Options left null entirely

        await stage.ExecuteAsync(context);

        Assert.DoesNotContain("<script", context.Html, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AllowScriptsTrue_PreservesScriptTagsForChartRendering()
    {
        var context = await SanitizeAsync(
            "<html><head><script src=\"https://cdnjs.cloudflare.com/ajax/libs/Chart.js/4.4.0/chart.umd.min.js\"></script></head>" +
            "<body><canvas id=\"c\"></canvas><script>new Chart(document.getElementById('c'), {type:'bar'});</script></body></html>",
            allowScripts: true);

        Assert.Contains("<script", context.Html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cdnjs.cloudflare.com", context.Html);
        Assert.Contains("new Chart(", context.Html);
    }

    [Fact]
    public async Task ExecuteAsync_AllowScriptsTrue_StillStripsInlineEventHandlersAndJavascriptUris()
    {
        var context = await SanitizeAsync(
            "<html><body><script>var x=1;</script><img src=\"a.png\" onerror=\"alert(1)\"><a href=\"javascript:alert(1)\">link</a></body></html>",
            allowScripts: true);

        Assert.Contains("<script", context.Html, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", context.Html, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", context.Html, System.StringComparison.OrdinalIgnoreCase);
    }
}
