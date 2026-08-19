using System.Text.Json.Serialization;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.DTOs;

public class GeneratePdfRequest
{
    public string DocumentName { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    
    [JsonPropertyName("html")]
    public string HtmlContent { get; set; } = string.Empty;

    // Render a live web page instead of supplying HTML directly. Exactly one of
    // html/url is expected per request.
    public string? Url { get; set; }

    // When set, `html` is treated as a Scriban template (https://github.com/scriban/scriban
    // — {{ name }}, {{ for item in items }}...{{ end }}, {{ if cond }}...{{ end }}) and
    // rendered against this JSON data before anything else in the pipeline runs. Lets a
    // caller keep one reusable HTML template and supply just the data per request,
    // instead of building a full HTML string themselves every time.
    public System.Text.Json.JsonElement? TemplateData { get; set; }

    public RenderingOptions Options { get; set; } = new RenderingOptions();

    public string? CorrelationId { get; set; }
    public string? SourceType { get; set; } = "API";
    public string? SdkLanguage { get; set; }
    public string? SdkVersion { get; set; }
}
