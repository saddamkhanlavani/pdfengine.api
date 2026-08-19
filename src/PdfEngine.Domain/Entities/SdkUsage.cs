using System;

namespace PdfEngine.Domain.Entities;

public class SdkUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string Language { get; set; } = string.Empty; // Node, Python, C#, Go
    public string Version { get; set; } = string.Empty;
    public int CallCount { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
