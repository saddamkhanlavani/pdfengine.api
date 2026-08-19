using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Application.Validators;
using PdfEngine.Domain.Enums;
using Xunit;

namespace PdfEngine.UnitTests;

public class GeneratePdfCommandValidatorTests
{
    private readonly GeneratePdfCommandValidator _validator = new();

    private static GeneratePdfCommand BaseCommand() => new()
    {
        DocumentName = "test-doc",
        DocumentType = DocumentType.Custom
    };

    [Fact]
    public void Validate_HtmlOnly_IsValid()
    {
        var command = BaseCommand();
        command.HtmlContent = "<html></html>";

        var result = _validator.Validate(command);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UrlOnly_IsValid()
    {
        var command = BaseCommand();
        command.Url = "https://example.com/report";

        var result = _validator.Validate(command);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NeitherHtmlNorUrl_IsInvalid()
    {
        var command = BaseCommand();

        var result = _validator.Validate(command);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_BothHtmlAndUrl_IsInvalid()
    {
        var command = BaseCommand();
        command.HtmlContent = "<html></html>";
        command.Url = "https://example.com";

        var result = _validator.Validate(command);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_NonHttpUrl_IsInvalid()
    {
        var command = BaseCommand();
        command.Url = "javascript:alert(1)";

        var result = _validator.Validate(command);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_RelativeUrl_IsInvalid()
    {
        var command = BaseCommand();
        command.Url = "/just/a/path";

        var result = _validator.Validate(command);

        Assert.False(result.IsValid);
    }
}
