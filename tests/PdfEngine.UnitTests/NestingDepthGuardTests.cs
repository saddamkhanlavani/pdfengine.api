using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Application.Validators;
using PdfEngine.Domain.Enums;
using Xunit;

namespace PdfEngine.UnitTests;

/// <summary>
/// Regression tests for the deep-nesting denial of service found by Release Gate I.
///
/// The shipped defect: a single authenticated request containing deeply nested HTML
/// crashed the ENTIRE API process, taking every other tenant's in-flight render with it.
/// AngleSharp's Node.Normalize recurses once per nesting level, and ~6,000 nested divs
/// produced "Stack overflow. Repeat 4009 times: at AngleSharp.Dom.Node.Normalize()".
/// A stack overflow is uncatchable in .NET, so no try/catch can make this safe after the
/// fact — the input has to be refused before a parser ever sees it.
/// </summary>
public class NestingDepthGuardTests
{
    private static GeneratePdfCommand Command(string html) => new()
    {
        DocumentName = "test",
        DocumentType = DocumentType.Custom,
        HtmlContent = html
    };

    private static string Nested(int depth) =>
        "<html><body>" + string.Concat(System.Linq.Enumerable.Repeat("<div>", depth))
        + "content"
        + string.Concat(System.Linq.Enumerable.Repeat("</div>", depth)) + "</body></html>";

    [Fact]
    public void OrdinaryDocument_IsAccepted()
    {
        var html = "<html><body><div><section><article><p>Normal <b>content</b>.</p>"
                 + "</article></section></div></body></html>";

        Assert.True(new GeneratePdfCommandValidator().Validate(Command(html)).IsValid);
    }

    [Fact]
    public void DeeplyNestedDocument_IsRejected()
    {
        var result = new GeneratePdfCommandValidator().Validate(Command(Nested(6000)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("nesting depth"));
    }

    [Fact]
    public void DepthJustUnderTheLimit_IsAccepted()
    {
        // The guard must reject attacks without rejecting deep-but-legitimate markup,
        // so the boundary is asserted from both sides.
        Assert.True(new GeneratePdfCommandValidator()
            .Validate(Command(Nested(GeneratePdfCommandValidator.MaxHtmlNestingDepth - 8)))
            .IsValid);
    }

    [Fact]
    public void VoidElementsDoNotCountTowardDepth()
    {
        // A long run of <br>/<img> opens no levels. Counting them would report ordinary
        // documents as deeply nested and refuse perfectly valid work.
        var html = "<html><body>" + string.Concat(
            System.Linq.Enumerable.Repeat("<br><img src='x'><hr>", 4000)) + "</body></html>";

        Assert.True(GeneratePdfCommandValidator.MeasureMaxNestingDepth(html) < 10);
        Assert.True(new GeneratePdfCommandValidator().Validate(Command(html)).IsValid);
    }

    [Fact]
    public void SelfClosingTagsDoNotCountTowardDepth()
    {
        var html = "<html><body>" + string.Concat(
            System.Linq.Enumerable.Repeat("<svg/>", 4000)) + "</body></html>";

        Assert.True(GeneratePdfCommandValidator.MeasureMaxNestingDepth(html) < 10);
    }

    [Theory]
    [InlineData("<html><body><div><div></div></div></body></html>", 4)]
    [InlineData("<p>flat</p>", 1)]
    [InlineData("", 0)]
    public void DepthIsMeasuredCorrectly(string html, int expected)
    {
        Assert.Equal(expected, GeneratePdfCommandValidator.MeasureMaxNestingDepth(html));
    }
}
