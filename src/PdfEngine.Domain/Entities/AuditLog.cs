using System;

namespace PdfEngine.Domain.Entities;

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Metadata { get; set; } // JSON string
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation property
    public Tenant? Tenant { get; set; }
}
