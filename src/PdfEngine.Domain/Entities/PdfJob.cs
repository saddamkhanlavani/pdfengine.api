using System;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Domain.Entities;

public class PdfJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public string ApiKey { get; set; } = string.Empty;
    
    public string EncryptedHtmlContent { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    
    public PdfJobStatus Status { get; set; } = PdfJobStatus.Queued;
    public string? FileUrl { get; set; }
    public string? ErrorMessage { get; set; }
    
    public int RetryCount { get; set; }
    public int MaxRetries { get; set; } = 3;
    public bool IsDeadLetter { get; set; }
    public int Priority { get; set; } = 1; // 0=Low, 1=Normal, 2=High, 3=Enterprise
    
    public string CorrelationId { get; set; } = string.Empty;
    public long QueueWaitDurationMs { get; set; }
    
    public string RendererVersion { get; set; } = "1.0.0";
    public string SourceType { get; set; } = "API"; // API, Playground, etc.
    public string? SdkLanguage { get; set; }
    public string? SdkVersion { get; set; }
    
    public string Environment { get; set; } = "Production"; // "Production" or "Development"
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
