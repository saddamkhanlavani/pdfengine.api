using System.Text.Json.Serialization;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.DTOs;

public class GeneratePdfRequest
{
    public string DocumentName { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    
    [JsonPropertyName("html")]
    public string HtmlContent { get; set; } = string.Empty;
    
    public RenderingOptions Options { get; set; } = new RenderingOptions();

    public string? CorrelationId { get; set; }
    public string? SourceType { get; set; } = "API";
    public string? SdkLanguage { get; set; }
    public string? SdkVersion { get; set; }
}
