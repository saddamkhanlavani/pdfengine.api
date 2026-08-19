using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class GeneratePdfCommand : IRequest<Result<byte[]>>
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public Tenant Client { get; set; } = null!;

    public ApiKey? ApiKey { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    public string HtmlContent { get; set; } = string.Empty;

    // Alternate content source: render a live web page instead of a supplied HTML
    // string. Exactly one of HtmlContent/Url is expected — enforced by the validator.
    public string? Url { get; set; }

    // When set, HtmlContent is treated as a Scriban template and rendered against
    // this data before the rest of the pipeline sees it.
    public System.Text.Json.JsonElement? TemplateData { get; set; }

    public RenderingOptions Options { get; set; } = new RenderingOptions();
    
    // Auxiliary output property for diagnostics
    public GeneratePdfDiagnostics Diagnostics { get; set; } = new();
}

public class GeneratePdfDiagnostics
{
    public List<AssetLog> Assets { get; set; } = new();
    public List<string> JsErrors { get; set; } = new();
    public List<string> ConsoleWarnings { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int RenderScore { get; set; } = 100;
    public int LayoutScore { get; set; } = 25;
    public int AssetScore { get; set; } = 25;
    public int PerformanceScore { get; set; } = 25;
    public int ConsistencyScore { get; set; } = 25;
    public int Pages { get; set; }
    public long FileSize { get; set; }
    public long DurationMs { get; set; }
    public string? HarJson { get; set; }
    public double VisualDrift { get; set; }
    public string? ScreenshotBase64 { get; set; }
    public double EstimatedCost { get; set; }
    
    // Extended diagnostics
    public List<PageDiagnostics> BlankPagesDetail { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public List<string> EmbeddedFonts { get; set; } = new();
}

public class PageDiagnostics
{
    public int Page { get; set; }
    public int TextNodes { get; set; }
    public int Images { get; set; }
    public int Elements { get; set; }
}

public class AssetLog
{
    public string Url { get; set; } = string.Empty;
    public int Status { get; set; } // HTTP status code
    public string StatusString { get; set; } = "loaded"; // "loaded", "failed"
    public string? Reason { get; set; } // "404", "Connection Timeout", etc.
    public string Type { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public long SizeBytes { get; set; }
}
