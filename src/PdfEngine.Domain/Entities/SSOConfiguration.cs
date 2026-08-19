using System;

namespace PdfEngine.Domain.Entities;

public class SSOConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string Provider { get; set; } = "Google"; // OIDC, SAML, Google, Microsoft
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecretEncrypted { get; set; } = string.Empty;
    public string? MetadataUrl { get; set; }
    
    public bool IsEnabled { get; set; } = false;
}
