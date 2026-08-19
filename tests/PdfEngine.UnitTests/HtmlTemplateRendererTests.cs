using System.Text.Json;
using PdfEngine.Application.Common;
using Xunit;

namespace PdfEngine.UnitTests;

public class HtmlTemplateRendererTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Render_SimpleVariable_Substitutes()
    {
        var (success, result, error) = HtmlTemplateRenderer.Render(
            "<h1>Invoice for {{ customer_name }}</h1>",
            Parse("{\"customer_name\": \"Acme Corp\"}"));

        Assert.True(success, error);
        Assert.Equal("<h1>Invoice for Acme Corp</h1>", result);
    }

    [Fact]
    public void Render_Loop_ProducesRepeatedContent()
    {
        var (success, result, error) = HtmlTemplateRenderer.Render(
            "<ul>{{ for item in items }}<li>{{ item.name }}: ${{ item.price }}</li>{{ end }}</ul>",
            Parse("{\"items\": [{\"name\": \"Widget\", \"price\": 10}, {\"name\": \"Gadget\", \"price\": 20}]}"));

        Assert.True(success, error);
        Assert.Equal("<ul><li>Widget: $10</li><li>Gadget: $20</li></ul>", result);
    }

    [Fact]
    public void Render_Conditional_TakesTheTrueBranch()
    {
        var (success, result, error) = HtmlTemplateRenderer.Render(
            "{{ if paid }}<span>PAID</span>{{ else }}<span>DUE</span>{{ end }}",
            Parse("{\"paid\": true}"));

        Assert.True(success, error);
        Assert.Equal("<span>PAID</span>", result);
    }

    [Fact]
    public void Render_MissingVariable_RendersEmptyRatherThanThrowing()
    {
        var (success, result, error) = HtmlTemplateRenderer.Render(
            "<p>Hello {{ missing_name }}</p>",
            Parse("{}"));

        Assert.True(success, error);
        Assert.Equal("<p>Hello </p>", result);
    }

    [Fact]
    public void Render_MalformedTemplate_FailsWithClearError()
    {
        var (success, _, error) = HtmlTemplateRenderer.Render(
            "<p>{{ if unclosed </p>",
            Parse("{}"));

        Assert.False(success);
        Assert.NotNull(error);
    }

    [Fact]
    public void Render_NoTemplateData_LeavesLiteralTextAlone()
    {
        var (success, result, error) = HtmlTemplateRenderer.Render("<p>Static content, no placeholders.</p>", null);

        Assert.True(success, error);
        Assert.Equal("<p>Static content, no placeholders.</p>", result);
    }
}
