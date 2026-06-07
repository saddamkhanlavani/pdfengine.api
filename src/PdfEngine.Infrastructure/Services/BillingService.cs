using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;
using PdfEngine.Infrastructure.Data;
using Stripe;

namespace PdfEngine.Infrastructure.Services;

public class BillingService : IBillingService
{
    private readonly PdfEngineDbContext _context;
    private readonly ILogger<BillingService> _logger;

    public BillingService(PdfEngineDbContext context, ILogger<BillingService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<PdfEngine.Domain.Entities.Invoice> GenerateInvoiceAsync(Guid tenantId)
    {
        var tenant = await _context.Tenants.FindAsync(tenantId);
        if (tenant == null) throw new ArgumentException("Tenant not found");

        var plan = PlanRegistry.Plans[tenant.Plan];
        var start = tenant.BillingCycleStart;
        var end = DateTime.UtcNow;

        var existing = await _context.Invoices
            .AnyAsync(i => i.TenantId == tenantId && i.PeriodStart == start);
        
        if (existing)
        {
            return await _context.Invoices
                .FirstAsync(i => i.TenantId == tenantId && i.PeriodStart == start);
        }

        var totalRequests = await _context.UsageRecords
            .Where(x => x.TenantId == tenantId && x.Timestamp >= start && x.Timestamp < end && x.Success)
            .CountAsync();

        var overageRequests = Math.Max(0, totalRequests - plan.IncludedQuota);
        var overageCost = overageRequests * plan.OveragePricePerPdf;

        var invoice = new PdfEngine.Domain.Entities.Invoice
        {
            TenantId = tenantId,
            PeriodStart = start,
            PeriodEnd = end,
            TotalRequests = totalRequests,
            IncludedQuota = plan.IncludedQuota,
            OverageRequests = overageRequests,
            OverageCost = overageCost,
            TotalAmount = overageCost,
            Status = InvoiceStatus.Open,
            GeneratedAt = DateTime.UtcNow
        };

        _context.Invoices.Add(invoice);
        await _context.SaveChangesAsync();

        return invoice;
    }

    public async Task ProcessDailyInvoicesAsync()
    {
        var tenantsToBill = await _context.Tenants
            .Where(t => t.Status == TenantStatus.Active && DateTime.UtcNow >= t.BillingCycleStart.AddMonths(1))
            .ToListAsync();

        foreach (var tenant in tenantsToBill)
        {
            try
            {
                await GenerateInvoiceAsync(tenant.Id);
                tenant.BillingCycleStart = DateTime.UtcNow;
                _context.Update(tenant);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate invoice for tenant {TenantId}", tenant.Id);
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task<string> CreateStripeCustomerAsync(Guid tenantId, string email)
    {
        var tenant = await _context.Tenants.FindAsync(tenantId);
        if (tenant == null) throw new Exception("Tenant not found");

        var options = new CustomerCreateOptions
        {
            Email = email,
            Name = tenant.Name,
            Metadata = new System.Collections.Generic.Dictionary<string, string>
            {
                { "TenantId", tenantId.ToString() }
            }
        };

        var service = new CustomerService();
        var customer = await service.CreateAsync(options);

        tenant.StripeCustomerId = customer.Id;
        await _context.SaveChangesAsync();

        return customer.Id;
    }

    public async Task UpdateSubscriptionStatusAsync(string stripeSubscriptionId, string status)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.StripeSubscriptionId == stripeSubscriptionId);
        if (tenant == null) return;

        tenant.Status = status switch
        {
            "active" => TenantStatus.Active,
            "trialing" => TenantStatus.Trialing,
            "past_due" => TenantStatus.PastDue,
            "unpaid" => TenantStatus.Suspended,
            "canceled" => TenantStatus.Cancelled,
            _ => tenant.Status
        };

        if (tenant.Status == TenantStatus.Suspended || tenant.Status == TenantStatus.Cancelled)
        {
            tenant.SuspendedAt = DateTime.UtcNow;
            _logger.LogWarning("Tenant {TenantName} SUSPENDED due to subscription status: {Status}", tenant.Name, status);
        }

        await _context.SaveChangesAsync();
    }

    public async Task HandlePaymentSuccessAsync(string stripeInvoiceId)
    {
        var invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.StripeInvoiceId == stripeInvoiceId);
        if (invoice == null) return;

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = DateTime.UtcNow;

        var tenant = await _context.Tenants.FindAsync(invoice.TenantId);
        if (tenant != null && tenant.Status == TenantStatus.PastDue)
        {
            tenant.Status = TenantStatus.Active;
        }

        await _context.SaveChangesAsync();
    }

    public async Task HandlePaymentFailureAsync(string stripeInvoiceId)
    {
        var invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.StripeInvoiceId == stripeInvoiceId);
        if (invoice == null) return;

        invoice.Status = InvoiceStatus.Failed;
        
        var tenant = await _context.Tenants.FindAsync(invoice.TenantId);
        if (tenant != null)
        {
            tenant.Status = TenantStatus.PastDue;
            _logger.LogWarning("Tenant {TenantName} marked as PAST DUE for invoice {InvoiceId}", tenant.Name, stripeInvoiceId);
        }

        await _context.SaveChangesAsync();
    }

    public async Task UpgradePlanAsync(Guid tenantId, string newPriceId)
    {
        var tenant = await _context.Tenants.FindAsync(tenantId);
        if (tenant == null || string.IsNullOrEmpty(tenant.StripeSubscriptionId)) return;

        var service = new SubscriptionService();
        var subscription = await service.GetAsync(tenant.StripeSubscriptionId);

        var options = new SubscriptionUpdateOptions
        {
            Items = new System.Collections.Generic.List<SubscriptionItemOptions>
            {
                new SubscriptionItemOptions
                {
                    Id = subscription.Items.Data[0].Id,
                    Price = newPriceId,
                },
            },
            ProrationBehavior = "always_invoice",
        };

        await service.UpdateAsync(tenant.StripeSubscriptionId, options);

        // Update local plan state
        tenant.Plan = MapPriceIdToPlan(newPriceId);
        
        await _context.SaveChangesAsync();
    }

    private PlanType MapPriceIdToPlan(string priceId)
    {
        // In a real app, these would be in config or DB
        return priceId switch
        {
            "price_pro_id" => PlanType.Pro,
            "price_enterprise_id" => PlanType.Enterprise,
            _ => PlanType.Free
        };
    }
}
