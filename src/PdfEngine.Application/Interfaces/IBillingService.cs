using System;
using System.Threading.Tasks;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Interfaces;

public interface IBillingService
{
    // Local Invoicing
    Task<Invoice> GenerateInvoiceAsync(Guid tenantId);
    Task ProcessDailyInvoicesAsync();

    // Stripe Operations
    Task<string> CreateStripeCustomerAsync(Guid tenantId, string email);
    Task UpdateSubscriptionStatusAsync(string stripeSubscriptionId, string status);
    Task HandlePaymentSuccessAsync(string stripeInvoiceId);
    Task HandlePaymentFailureAsync(string stripeInvoiceId);
    
    // Plan Management
    Task UpgradePlanAsync(Guid tenantId, string newPriceId);
}
