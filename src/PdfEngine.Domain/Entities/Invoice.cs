using System;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Domain.Entities;

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string? StripeInvoiceId { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int TotalRequests { get; set; }
    public int IncludedQuota { get; set; }
    public int OverageRequests { get; set; }
    public decimal OverageCost { get; set; }
    public decimal TotalAmount { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    
    // Navigation property
    public Tenant? Tenant { get; set; }
}
