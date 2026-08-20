using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Workers;

public class BillingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingWorker> _logger;
    private readonly IConfiguration _configuration;

    public BillingWorker(IServiceProvider serviceProvider, ILogger<BillingWorker> logger,
                         IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
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

                // 0. Retention. Opt-in, and payload-only by default.
                await EnforceRetentionAsync(dbContext, stoppingToken);

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

    /// <summary>
    /// Clears stored request payloads once a tenant's retention window has passed.
    ///
    /// `TenantEntitlement.RetentionDays` existed as a field with nothing enforcing it, so
    /// every HTML payload ever submitted was kept indefinitely. Found by exercising the
    /// backup procedure rather than by any gate: a development database held 770 MB of
    /// `EncryptedHtmlContent` across 47,226 jobs, and the dump was 666 MB. Two problems in
    /// one — backups that grow without bound, and customer HTML (which can contain personal
    /// data) retained forever with no policy behind it.
    ///
    /// Two deliberate constraints:
    ///
    ///   OPT-IN.        `Retention:Enabled` defaults to false. Deleting customer data on a
    ///                  schedule is not something a service should start doing because it
    ///                  was upgraded.
    ///   PAYLOAD ONLY.  The job row survives — status, timings, usage and audit history are
    ///                  what invoices and support questions are answered from. Only the
    ///                  submitted content is cleared, which is the part that is both large
    ///                  and sensitive. `Retention:DeleteJobRows` will remove whole rows for
    ///                  operators who need that, and says so in its name.
    /// </summary>
    private async Task EnforceRetentionAsync(PdfEngineDbContext dbContext, CancellationToken token)
    {
        if (!_configuration.GetValue<bool>("Retention:Enabled"))
        {
            _logger.LogDebug("Retention is disabled (Retention:Enabled=false); stored payloads are kept indefinitely.");
            return;
        }

        var fallbackDays = _configuration.GetValue("Retention:DefaultDays", 90);
        var deleteRows = _configuration.GetValue<bool>("Retention:DeleteJobRows");
        var batchSize = Math.Clamp(_configuration.GetValue("Retention:BatchSize", 5000), 100, 50000);

        // Per tenant, because retention is an entitlement and plans differ.
        var windows = await dbContext.Set<TenantEntitlement>()
            .Where(e => e.RetentionDays > 0)
            .Select(e => new { e.TenantId, e.RetentionDays })
            .ToListAsync(token);
        var byTenant = windows.ToDictionary(w => w.TenantId, w => w.RetentionDays);

        var tenants = await dbContext.Tenants.Select(t => t.Id).ToListAsync(token);
        var clearedTotal = 0;

        foreach (var tenantId in tenants)
        {
            if (token.IsCancellationRequested) return;
            var days = byTenant.TryGetValue(tenantId, out var d) ? d : fallbackDays;
            if (days <= 0) continue;
            var cutoff = DateTime.UtcNow.AddDays(-days);

            // The batch is selected FIRST and the bulk operation is keyed off those ids.
            // ExecuteUpdate/ExecuteDelete reject Take() in EF Core 8 — they throw at
            // runtime, not at compile time, so the obvious one-query version compiles
            // cleanly and fails the first night it runs unattended.
            var batch = await dbContext.PdfJobs
                .Where(j => j.TenantId == tenantId
                            && j.CreatedAt < cutoff
                            && (deleteRows
                                || (j.EncryptedHtmlContent != null && j.EncryptedHtmlContent != "")))
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.JobId)
                .Take(batchSize)
                .ToListAsync(token);

            if (batch.Count == 0) continue;

            if (deleteRows)
            {
                clearedTotal += await dbContext.PdfJobs
                    .Where(j => batch.Contains(j.JobId))
                    .ExecuteDeleteAsync(token);
                continue;
            }

            // Only rows that still hold a payload, so this converges instead of rewriting
            // the same rows every twelve hours.
            clearedTotal += await dbContext.PdfJobs
                .Where(j => batch.Contains(j.JobId))
                // Cleared to an EMPTY STRING, not null: the column is NOT NULL, and the
                // null version compiles, reads correctly, and throws 23502 the first time
                // it runs. Found by pointing the worker at a restored copy of a real
                // database rather than by reading the code.
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.EncryptedHtmlContent, _ => string.Empty), token);
        }

        // Logged even when it does nothing. A retention job that is silently a no-op —
        // because no tenant matched, or every window is longer than the oldest row — looks
        // exactly like a retention job that is working, and the difference only becomes
        // visible when someone asks why the database never shrinks.
        _logger.LogInformation(
            "Retention pass: {Tenants} tenant(s), fallback {Days} day(s), mode {Mode}, {Count} job(s) affected.",
            tenants.Count, fallbackDays, deleteRows ? "delete rows" : "clear payload", clearedTotal);

        if (clearedTotal > 0)
        {
            _logger.LogInformation(
                "Retention: {Count} job(s) {Action} (batch limit {Batch}).",
                clearedTotal, deleteRows ? "deleted" : "had their stored payload cleared", batchSize);
        }
    }
}
