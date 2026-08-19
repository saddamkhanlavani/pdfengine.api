using System;

namespace PdfEngine.Domain.Entities;

public class UsageAggregate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTime Date { get; set; }
    public int SuccessfulRenders { get; set; }
    public int FailedRenders { get; set; }
    public double TotalLatencyMs { get; set; }

    // Navigation
    public Tenant? Tenant { get; set; }
}
