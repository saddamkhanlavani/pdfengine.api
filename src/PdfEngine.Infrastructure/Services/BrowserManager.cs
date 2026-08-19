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
    private IBrowserContext? _sharedContext;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;
    private int _renderCount = 0;

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

    public async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _renderCount++;
            if (_renderCount > 50)
            {
                _logger.LogInformation("Max render threshold (50) reached. Recycling browser instance in background...");
                await DisposeBrowserOnlyAsync();
                _renderCount = 1;
            }
            return await InitializeBrowserWithRetryAsync();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IBrowserContext> GetSharedContextAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _renderCount++;
            if (_renderCount > 50)
            {
                _logger.LogInformation("Max render threshold (50) reached. Recycling browser instance in background...");
                await DisposeBrowserOnlyAsync();
                _renderCount = 1;
            }
            var browser = await InitializeBrowserWithRetryAsync();
            if (_sharedContext == null)
            {
                _logger.LogInformation("Creating new shared browser context for connection/cache pooling...");
                _sharedContext = await browser.NewContextAsync();
            }
            return _sharedContext;
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
                        Args = new[]
                        {
                            "--disable-gpu",
                            "--disable-dev-shm-usage",
                            "--no-sandbox",
                            "--no-first-run",
                            "--no-zygote",
                            "--disable-extensions",
                            "--disable-back-forward-cache",
                            "--js-flags=--max-old-space-size=2048"
                        }
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
        if (_sharedContext != null)
        {
            var oldContext = _sharedContext;
            _sharedContext = null;
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Background context disposal scheduled. Waiting 15 seconds grace period...");
                    await Task.Delay(15000);
                    await oldContext.DisposeAsync();
                    _logger.LogInformation("Old context disposed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing old browser context in background.");
                }
            });
        }

        if (_browser != null)
        {
            var oldBrowser = _browser;
            _browser = null;

            // Dispose old browser instance asynchronously in background after a safe grace period of 15 seconds
            _ = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("Background browser disposal scheduled. Waiting 15 seconds grace period...");
                    await Task.Delay(15000);
                    _logger.LogInformation("Disposing old Chromium browser instance...");
                    await oldBrowser.DisposeAsync();
                    _logger.LogInformation("Old Chromium browser instance disposed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing old browser instance in background.");
                }
            });
        }
    }

    public async Task ForceRecycleBrowserAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            _logger.LogWarning("Forced browser recycling triggered.");
            await DisposeBrowserOnlyAsync();
            _renderCount = 0;
        }
        finally
        {
            _semaphore.Release();
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
