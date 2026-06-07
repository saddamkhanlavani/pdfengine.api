using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Enums;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Workers;

public class BillingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingWorker> _logger;

    public BillingWorker(IServiceProvider serviceProvider, ILogger<BillingWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BillingWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                if (stoppingToken.IsCancellationRequested) break;

                var billingService = scope.ServiceProvider.GetRequiredService<IBillingService>();
                var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngineDbContext>();

                // 1. Process standard daily invoices
                await billingService.ProcessDailyInvoicesAsync();

                // 2. ENFORCE GRACE PERIOD: Suspend tenants who are PastDue for > 7 days
                if (stoppingToken.IsCancellationRequested) break;

                var overdueTenants = await dbContext.Tenants
                    .Where(t => t.Status == TenantStatus.PastDue)
                    .ToListAsync(stoppingToken);

                foreach (var tenant in overdueTenants)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // Check if they have unpaid invoices older than 7 days
                    var hasOldUnpaidInvoices = await dbContext.Invoices
                        .AnyAsync(i => i.TenantId == tenant.Id && 
                                       i.Status != InvoiceStatus.Paid && 
                                       i.GeneratedAt < DateTime.UtcNow.AddDays(-7), 
                                  stoppingToken);

                    if (hasOldUnpaidInvoices)
                    {
                        tenant.Status = TenantStatus.Suspended;
                        tenant.SuspendedAt = DateTime.UtcNow;
                        _logger.LogWarning("Tenant {TenantName} automatically SUSPENDED due to unpaid invoices older than 7 days.", tenant.Name);
                    }
                }

                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during background billing tasks.");
            }

            // Check every 12 hours
            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }
}
