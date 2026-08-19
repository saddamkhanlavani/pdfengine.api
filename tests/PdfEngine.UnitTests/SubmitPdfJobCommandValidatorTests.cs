using PdfEngine.Application.DTOs;
using PdfEngine.Application.Features.Jobs.Commands;
using PdfEngine.Application.Validators;
using PdfEngine.Domain.Enums;
using Xunit;

namespace PdfEngine.UnitTests;

public class SubmitPdfJobCommandValidatorTests
{
    private readonly SubmitPdfJobCommandValidator _validator = new();

    private static SubmitPdfJobCommand CommandWith(GeneratePdfRequest request) => new() { Request = request };

    [Fact]
    public void Validate_HtmlOnly_IsValid()
    {
        var result = _validator.Validate(CommandWith(new GeneratePdfRequest
        {
            DocumentName = "doc",
            DocumentType = DocumentType.Custom,
            HtmlContent = "<html></html>"
        }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NeitherHtmlNorUrl_IsInvalid()
    {
        // This exact gap let a malformed batch item queue successfully instead of
        // being rejected immediately — reproduced and fixed.
        var result = _validator.Validate(CommandWith(new GeneratePdfRequest
        {
            DocumentName = "doc",
            DocumentType = DocumentType.Custom
        }));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_BothHtmlAndUrl_IsInvalid()
    {
        var result = _validator.Validate(CommandWith(new GeneratePdfRequest
        {
            DocumentName = "doc",
            DocumentType = DocumentType.Custom,
            HtmlContent = "<html></html>",
            Url = "https://example.com"
        }));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_InvalidPageSize_IsInvalid()
    {
        var result = _validator.Validate(CommandWith(new GeneratePdfRequest
        {
            DocumentName = "doc",
            DocumentType = DocumentType.Custom,
            HtmlContent = "<html></html>",
            Options = new RenderingOptions { PageSize = "NotAPageSize" }
        }));

        Assert.False(result.IsValid);
    }
}
