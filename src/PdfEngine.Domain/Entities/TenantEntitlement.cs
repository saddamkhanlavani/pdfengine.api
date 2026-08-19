using System;

namespace PdfEngine.Domain.Entities;

public class TenantEntitlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    
    // Limits
    public int MonthlyRenderLimit { get; set; }
    public int ConcurrentRenderLimit { get; set; }
    public int StorageLimitGb { get; set; }
    public int TeamMemberLimit { get; set; }
    public int ApiKeyLimit { get; set; }
    public int WebhookLimit { get; set; }
    public int RetentionDays { get; set; }
    public int RequestsPerMinute { get; set; }

    // Navigation
    public Tenant? Tenant { get; set; }
}
