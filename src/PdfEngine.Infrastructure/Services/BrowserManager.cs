using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PdfEngine.Infrastructure.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public sealed class BrowserManager : IBrowserManager, IAsyncDisposable
{
    private readonly ILogger<BrowserManager> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public BrowserManager(ILogger<BrowserManager> logger)
    {
        _logger = logger;
    }

    public bool IsBrowserAlive()
    {
        // If it's a cold start (_browser == null), consider it healthy because it will initialize on first request.
        // Only return false if it was running and died.
        if (_browser == null) return true;
        return _browser.IsConnected;
    }

    private int _renderCount = 0;

    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _renderCount++;
            if (_renderCount > 50)
            {
                _logger.LogInformation("Max render threshold (50) reached. Recycling browser instance...");
                await DisposeBrowserOnlyAsync();
                _renderCount = 1; // reset count starting from current request
            }
            return await InitializeBrowserWithRetryAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<IBrowser> InitializeBrowserWithRetryAsync()
    {
        if (_browser != null && _browser.IsConnected)
            return _browser;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (_browser == null || !_browser.IsConnected)
                {
                    if (_browser != null)
                    {
                        _logger.LogWarning("Reinitializing Chromium...");
                        await DisposeBrowserOnlyAsync();
                    }
                    else
                    {
                        _logger.LogInformation("Initializing Playwright and launching headless Chromium...");
                    }

                    _playwright ??= await Playwright.CreateAsync();
                    
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                    });

                    _browser.Disconnected += OnBrowserDisconnected;
                    _logger.LogInformation("Chromium launched successfully.");
                }

                return _browser;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch Chromium browser on attempt {Attempt}.", attempt);
                await DisposeBrowserOnlyAsync();
                if (attempt == 2) throw;
            }
        }
        
        throw new InvalidOperationException("Failed to initialize browser after retries.");
    }

    private void OnBrowserDisconnected(object? sender, IBrowser browser)
    {
        _logger.LogWarning("Chromium browser disconnected unexpectedly.");
    }

    private async Task DisposeBrowserOnlyAsync()
    {
        if (_browser != null)
        {
            try { await _browser.DisposeAsync(); } catch { /* Ignore */ }
            _browser = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _logger.LogInformation("Disposing BrowserManager. Shutting down Chromium...");
        await _semaphore.WaitAsync();
        try
        {
            await DisposeBrowserOnlyAsync();
            if (_playwright != null)
            {
                _playwright.Dispose();
                _playwright = null;
            }
            _disposed = true;
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}
