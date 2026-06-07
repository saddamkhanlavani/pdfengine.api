using System;

namespace PdfEngine.Domain.Entities;

public class ApiKey
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty; // Legacy key storage support or full text representation (we hash key secrets)
    public string KeyPrefix { get; set; } = string.Empty; // e.g. pk_live_abc12
    public string KeyHash { get; set; } = string.Empty; // SHA256 hashed secret
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public bool IsRevoked { get; set; }
    
    public string Scopes { get; set; } = "render:pdf,logs:read";
    public string? IpWhitelist { get; set; }
    public string Environment { get; set; } = "Production"; // "Production" or "Development"
    
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
}
