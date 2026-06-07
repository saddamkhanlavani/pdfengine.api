using System;

namespace PdfEngine.Domain.Entities;

public class WebhookEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    
    // Comma-separated events list, e.g. "render.completed,render.failed"
    public string Events { get; set; } = "render.completed,render.failed";
    
    // Environment context: "Development" or "Production"
    public string Environment { get; set; } = "Production";
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }
}
