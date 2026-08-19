using System;

namespace PdfEngine.Domain.Entities;

public class SCIMProvisioning
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string ScimApiKeyHash { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = false;
}
