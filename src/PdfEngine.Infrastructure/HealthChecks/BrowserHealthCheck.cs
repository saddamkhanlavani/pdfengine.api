using Microsoft.Extensions.Diagnostics.HealthChecks;
using PdfEngine.Infrastructure.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace PdfEngine.Infrastructure.HealthChecks;

public class BrowserHealthCheck : IHealthCheck
{
    private readonly IBrowserManager _browserManager;

    public BrowserHealthCheck(IBrowserManager browserManager)
    {
        _browserManager = browserManager;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_browserManager.IsBrowserAlive())
        {
            return Task.FromResult(HealthCheckResult.Healthy("Chromium is connected and ready."));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("Chromium is not available or disconnected."));
    }
}
