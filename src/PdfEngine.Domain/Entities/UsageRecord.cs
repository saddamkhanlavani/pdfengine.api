using System;

namespace PdfEngine.Domain.Entities;

public class UsageRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public Guid? ApiKeyId { get; set; }
    public ApiKey? ApiKey { get; set; }
    
    public string RequestId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    
    public int PdfSizeBytes { get; set; }
    public int DurationMs { get; set; }
    public int StatusCode { get; set; }
    
    public decimal Cost { get; set; }
    
    // Enterprise Observability & Diagnostics
    public string Environment { get; set; } = "Production";
    public string? EncryptedHtmlSnapshot { get; set; }
    public string? AssetsWaterfall { get; set; } // JSON list
    public string? RenderWarnings { get; set; }
    public int PageCount { get; set; }
    public double MemoryUsageMb { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string RendererVersion { get; set; } = "1.0.0";
    public string SourceType { get; set; } = "API"; // API, Playground, SDK, etc.
    public string? SdkLanguage { get; set; }
    public string? SdkVersion { get; set; }
}
