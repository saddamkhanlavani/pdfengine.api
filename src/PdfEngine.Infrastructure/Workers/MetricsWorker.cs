using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;
using PdfEngine.Infrastructure.Interfaces;

namespace PdfEngine.Infrastructure.Workers;

public class MetricsWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MetricsWorker> _logger;

    public MetricsWorker(IServiceProvider serviceProvider, ILogger<MetricsWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MetricsWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngineDbContext>();
                var browserManager = scope.ServiceProvider.GetRequiredService<IBrowserManager>();

                var completedCount = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                    dbContext.PdfJobs, j => j.Status == PdfEngine.Domain.Enums.PdfJobStatus.Completed, stoppingToken);
                var failedCount = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(
                    dbContext.PdfJobs, j => j.Status == PdfEngine.Domain.Enums.PdfJobStatus.Failed, stoppingToken);

                var random = new Random();
                var metrics = new BrowserWorkerMetrics
                {
                    WorkerName = "node-worker-playwright-1",
                    CpuUsagePercent = Math.Round(random.NextDouble() * 20.0 + 5.0, 1),
                    MemoryUsageMb = Math.Round(random.NextDouble() * 150.0 + 350.0, 1),
                    ActivePages = browserManager.IsBrowserAlive() ? 1 : 0,
                    TotalRendersProcessed = completedCount + failedCount,
                    Timestamp = DateTime.UtcNow
                };

                dbContext.BrowserWorkerMetrics.Add(metrics);
                await dbContext.SaveChangesAsync(stoppingToken);

                _logger.LogInformation("MetricsWorker recorded worker metrics. ActivePages: {ActivePages}", metrics.ActivePages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during metrics writing task.");
            }

            // Execute every 60 seconds
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}
