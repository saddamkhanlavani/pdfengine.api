using System;
using System.Collections.Generic;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public PlanType Plan { get; set; } = PlanType.Free;
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    
    // Auth & Security
    public string? TwoFactorSecret { get; set; }
    public bool IsTwoFactorEnabled { get; set; }
    public string PasswordHash { get; set; } = string.Empty;

    // Notification Settings
    public bool NotifyOn80Percent { get; set; } = true;
    public bool NotifyOn100Percent { get; set; } = true;
    public bool NotifyOnNewInvoice { get; set; } = true;

    // Usage Limits
    public int MonthlyHardLimit { get; set; } = 100000;
    public bool AutoTopUpEnabled { get; set; } = false;

    public DateTime BillingCycleStart { get; set; } = DateTime.UtcNow;
    
    // Stripe integration
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }

    // Organization Branding and Regional Settings
    public string BrandingColor { get; set; } = "#3b82f6";
    public string Locale { get; set; } = "en-US";
    public string Timezone { get; set; } = "UTC";
    public string? CustomLogoUrl { get; set; }

    public List<ApiKey> ApiKeys { get; set; } = new();
    public List<TwoFactorRecoveryCode> TwoFactorRecoveryCodes { get; set; } = new();
    
    public DateTime? SuspendedAt { get; set; }
    public bool IsActive => Status != TenantStatus.Suspended && Status != TenantStatus.Cancelled;
}
