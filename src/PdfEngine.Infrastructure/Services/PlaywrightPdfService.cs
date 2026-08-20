using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using SkiaSharp;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Enums;
using PdfEngine.Infrastructure.Interfaces;
using Prometheus;

namespace PdfEngine.Infrastructure.Services;

public class PlaywrightPdfService : IPdfService
{
    private readonly IBrowserManager _browserManager;
    private readonly ILogger<PlaywrightPdfService> _logger;
    private readonly IHtmlSanitizerStage _htmlSanitizerStage;
    private readonly IAssetOptimizerStage _assetOptimizerStage;
    private readonly IDomAnalyzer _domAnalyzer;
    private readonly ILayoutAnalyzer _layoutAnalyzer;
    private readonly IPaginationPlanner _paginationPlanner;
    private readonly ITypographyEngine _typographyEngine;

    // PHASE 10: Prometheus Metrics
    private static readonly Counter PdfGeneratedCounter = Metrics.CreateCounter(
        "pdf_generation_total", "Total number of PDFs generated", 
        new CounterConfiguration { LabelNames = new[] { "tenant", "status" } });

    private static readonly Histogram PdfDurationHistogram = Metrics.CreateHistogram(
        "pdf_generation_duration_seconds", "Time taken to generate PDF",
        new HistogramConfiguration { LabelNames = new[] { "tenant" } });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (IPAddress[] IPs, DateTime Expiry)> DnsCache = new();
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DnsFailureCacheTtl = TimeSpan.FromSeconds(30);

    // Every outbound resource fetch is pinned to the specific IP we already validated
    // against the private-range blocklist. Without this, IsIpSafe only checks the IP
    // our own DNS lookup returned; Chromium would then perform its own, independent
    // DNS resolution when it actually opens the connection, and an attacker-controlled
    // DNS server could return a safe IP to our check and a private/loopback IP moments
    // later to the real connection (a classic TOCTOU / DNS-rebinding bypass).
    private static readonly HttpRequestOptionsKey<IPAddress> PinnedIpOptionKey = new("PdfEngine.PinnedIp");

    private static readonly HttpClient PinnedResourceHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // redirects are followed manually so each hop is re-validated
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellationToken) =>
        {
            if (!context.InitialRequestMessage.Options.TryGetValue(PinnedIpOptionKey, out var pinnedIp) || pinnedIp == null)
            {
                throw new InvalidOperationException("SSRF defense: no pre-validated IP was set for this outbound request.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(pinnedIp, context.DnsEndPoint.Port, cancellationToken);
                // For https, SocketsHttpHandler layers TLS on top of this stream itself,
                // validating the certificate against the original hostname (SNI), so
                // connecting via the pinned IP does not weaken certificate validation.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public PlaywrightPdfService(
        IBrowserManager browserManager, 
        ILogger<PlaywrightPdfService> logger,
        IHtmlSanitizerStage htmlSanitizerStage,
        IAssetOptimizerStage assetOptimizerStage,
        IDomAnalyzer domAnalyzer,
        ILayoutAnalyzer layoutAnalyzer,
        IPaginationPlanner paginationPlanner,
        ITypographyEngine typographyEngine)
    {
        _browserManager = browserManager;
        _logger = logger;
        _htmlSanitizerStage = htmlSanitizerStage;
        _assetOptimizerStage = assetOptimizerStage;
        _domAnalyzer = domAnalyzer;
        _layoutAnalyzer = layoutAnalyzer;
        _paginationPlanner = paginationPlanner;
        _typographyEngine = typographyEngine;
    }

    public async Task<Result<byte[]>> GenerateAsync(GeneratePdfCommand command, CancellationToken cancellationToken = default)
    {
        var tenantName = command.Client?.Name ?? "Anonymous";
        var planType = command.Client?.Plan ?? PlanType.Free;
        
        // Retrieve limits based on Plan registry
        if (!PlanRegistry.Plans.TryGetValue(planType, out var planConfig))
        {
            planConfig = PlanRegistry.Plans[PlanType.Free];
        }

        var stopwatch = Stopwatch.StartNew();
        var isUrlMode = !string.IsNullOrWhiteSpace(command.Url);

        // Run compiler rendering context and pre-rendering stages
        var renderingContext = new RenderingContext(command.HtmlContent, command.Diagnostics, cancellationToken)
        {
            Options = command.Options
        };

        // Url mode renders a live page directly — there's no HTML string to sanitize,
        // optimize, or analyze here (that page's own markup is what Chromium loads).
        // SSRF protection for the navigation itself comes from the same pinned-fetch
        // route handler that already guards every subresource request, below.
        if (!isUrlMode)
        {
            // These stages run BEFORE the attempt loop and were therefore outside the
            // render budget entirely — bounded only by the caller hanging up. Measured
            // with a 24 MB SVG found by tests/fuzz_gate.py: the planner ran for 342
            // seconds, holding a tenant render slot the whole time, while the 30s budget
            // that was supposed to bound the request sat unused inside the loop below.
            // One request, one worker, five and a half minutes.
            using var analysisCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            analysisCts.CancelAfter(planConfig.MaxRenderDurationSeconds * 1000);
            var analysisToken = analysisCts.Token;
            try
            {
                await _htmlSanitizerStage.ExecuteAsync(renderingContext, analysisToken);
                await _assetOptimizerStage.ExecuteAsync(renderingContext, analysisToken);
                await _domAnalyzer.ExecuteAsync(renderingContext, analysisToken);
                await _layoutAnalyzer.ExecuteAsync(renderingContext, analysisToken);
                await _typographyEngine.ExecuteAsync(renderingContext, analysisToken);
                await _paginationPlanner.ExecuteAsync(renderingContext, analysisToken);
            }
            catch (OperationCanceledException) when (analysisCts.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                return Result<byte[]>.Fail(Error.RenderTimeout(
                    $"Analysing the document exceeded the {planConfig.MaxRenderDurationSeconds}s budget. The document is too expensive to lay out — reduce the number of elements, image size or SVG complexity."));
            }
        }

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            var harTempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.har");
            bool captureHar = command.Options.CaptureHAR;
            IPage? page = null;
            IBrowserContext? tempContext = null;
            // Set by the SSRF route handler when it deliberately blocks a request.
            // A blocked main-document navigation throws a PlaywrightException just
            // like a real browser crash does — verified by testing that without this
            // distinction, a blocked navigation gets treated as transient and retried
            // 3 times (with a full browser-pool recycle each time) for no benefit,
            // since the destination is still blocked identically on every retry.
            var ssrfBlockedThisAttempt = false;

            try
            {
                // Allocate custom timeout context
                var timeoutMs = planConfig.MaxRenderDurationSeconds * 1000;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);

                // Gate J: a pinned engine version is asserted BEFORE any rendering work,
                // so a version mismatch costs a fast failure rather than a full render the
                // caller must then throw away.
                EnsureEngineVersionMatches(command.Options.PinEngineVersion,
                    await _browserManager.GetBrowserAsync(cancellationToken));

                // Timezone and locale are fixed at context creation in Playwright and
                // cannot be changed per page, so requesting either opts this render out of
                // the shared context. Documented as a cost, not hidden.
                var needsDedicatedContext = captureHar
                    || !string.IsNullOrWhiteSpace(command.Options.Timezone)
                    || !string.IsNullOrWhiteSpace(command.Options.Locale);

                if (needsDedicatedContext)
                {
                    var browser = await _browserManager.GetBrowserAsync(cancellationToken);
                    tempContext = await browser.NewContextAsync(new BrowserNewContextOptions
                    {
                        ExtraHTTPHeaders = new[]
                        {
                            new KeyValuePair<string, string>("X-PdfEngine-Render", "1.0.0")
                        },
                        RecordHarPath = captureHar ? harTempPath : null,
                        TimezoneId = string.IsNullOrWhiteSpace(command.Options.Timezone)
                            ? null : command.Options.Timezone,
                        Locale = string.IsNullOrWhiteSpace(command.Options.Locale)
                            ? null : command.Options.Locale
                    });
                    page = await tempContext.NewPageAsync();
                }
                else
                {
                    var sharedContext = await _browserManager.GetSharedContextAsync(cancellationToken);
                    page = await sharedContext.NewPageAsync();
                    await page.SetExtraHTTPHeadersAsync(new[]
                    {
                        new KeyValuePair<string, string>("X-PdfEngine-Render", "1.0.0")
                    });
                }

            page.SetDefaultNavigationTimeout(timeoutMs);
            page.SetDefaultTimeout(timeoutMs);

                // Measure the document the way it will be PRINTED, not the way a 1280px
                // browser window would show it. Two mismatches were closing here, and both
                // made every measurement the planner takes systematically wrong:
                //
                //   * the viewport was Playwright's 1280px default while an A4 page with
                //     20mm/16mm margins is ~660px wide, so text wrapped differently when it
                //     was measured than when it was rendered, and a `width: 100%` page
                //     float measured nearly twice its printed width;
                //   * the page was in SCREEN media while `page.PdfAsync` renders in PRINT
                //     media — which meant the `@media print` rules the engine injects for
                //     itself (orphans/widows, break-inside, repeating table headers) were
                //     invisible to the pass that measures against them, as was every
                //     print stylesheet the author wrote.
                //
                // Set before the content loads so fonts, images and charts settle at the
                // final width rather than reflowing underneath the measurement.
                if (!command.Options.FullHeight)
                {
                    try
                    {
                        await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });
                        await page.SetViewportSizeAsync(
                            (int)Math.Round(PaginationPlanner.ComputePrintableWidthPx(command.Options)),
                            (int)Math.Round(PaginationPlanner.ComputePrintableHeightPx(command.Options)));
                    }
                    catch (Exception ex)
                    {
                        // Measuring against the wrong box is a quality problem, not a
                        // correctness one — the PDF itself is laid out by Chromium either
                        // way — so this degrades rather than failing the render.
                        _logger.LogWarning(ex, "Could not switch the page to print media at the printable size; measurements fall back to the default viewport.");
                    }
                }

            // Gate J: freeze the ambient clock and randomness BEFORE any document script
            // runs. AddInitScriptAsync executes on every new document ahead of page
            // scripts, which is the only point where a library cannot have already read
            // the real Date or Math.random.
            var determinismScript = BuildDeterminismInitScript(command.Options);
            if (determinismScript != null)
            {
                await page.AddInitScriptAsync(determinismScript);
            }

                // NOTE: tried switching to print media + a print-width-matched viewport
                // here, to make Pass 2's screen-mode measurement match actual print
                // layout. Reverted — verified by testing that it made measurements
                // *more* wrong, not less: with @media print and an @page rule both
                // active, Chromium appears to apply real paginated fragmentation to the
                // on-screen layout itself (gaps between simulated pages), which breaks
                // Pass 2's "one continuous flow" assumption in a different way than the
                // screen/print width mismatch it was meant to fix. Left as a known,
                // documented gap rather than an unverified "fix" — see Tier 2/3 notes.

            // Enforce asset download sizes
                long totalDownloadedBytes = 0;
                long maxAssetBytes = (long)(planConfig.MaxAssetDownloadMb * 1024 * 1024);

                var requestStartTimes = new System.Collections.Concurrent.ConcurrentDictionary<IRequest, DateTime>();
                var assetLogs = new System.Collections.Concurrent.ConcurrentBag<AssetLog>();

                page.Request += (sender, req) =>
                {
                    requestStartTimes[req] = DateTime.UtcNow;
                };

                page.Response += (sender, response) =>
                {
                    var end = DateTime.UtcNow;
                    var start = requestStartTimes.TryGetValue(response.Request, out var s) ? s : end;
                    var durationMs = (long)(end - start).TotalMilliseconds;

                    long sizeBytes = 0;
                    if (response.Headers.TryGetValue("content-length", out var lenStr) && 
                        long.TryParse(lenStr, out var length))
                    {
                        sizeBytes = length;
                        var currentTotal = Interlocked.Add(ref totalDownloadedBytes, length);
                        if (currentTotal > maxAssetBytes)
                        {
                            _logger.LogWarning("Job exceeded asset download limit of {Max}MB", planConfig.MaxAssetDownloadMb);
                            // Throws to trigger failure block safely
                            throw new Exception($"Asset download size exceeded maximum limit of {planConfig.MaxAssetDownloadMb}MB on current plan.");
                        }

                        // Single asset large size warning (>5MB)
                        if (sizeBytes > 5 * 1024 * 1024)
                        {
                            command.Diagnostics.Warnings.Add($"Asset warning: Resource '{response.Url}' exceeds 5MB in size ({(sizeBytes / 1024.0 / 1024.0).ToString("0.0")}MB). Highly compressed images or smaller formats are recommended.");
                        }
                    }

                    // Timeout warning: resource took > 2500ms to download
                    if (durationMs > 2500)
                    {
                        command.Diagnostics.Warnings.Add($"Asset warning: Resource '{response.Url}' took {durationMs}ms to load, exceeding the 2500ms speed threshold.");
                    }

                    // Mixed HTTP Content warning
                    if (response.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                        !response.Url.Contains("localhost") && 
                        !response.Url.Contains("127.0.0.1"))
                    {
                        command.Diagnostics.Warnings.Add($"Asset warning: Mixed HTTP content detected for url: '{response.Url}'. Load assets over secure HTTPS connection instead.");
                    }

                    // Redirect Chain checking
                    try
                    {
                        var redirReq = response.Request.RedirectedFrom;
                        int redirectCount = 0;
                        while (redirReq != null)
                        {
                            redirectCount++;
                            redirReq = redirReq.RedirectedFrom;
                        }
                        if (redirectCount > 2)
                        {
                            command.Diagnostics.Warnings.Add($"Asset warning: URL '{response.Url}' has a redirect chain of length {redirectCount}. Consider using direct URLs to minimize render delays.");
                        }
                    }
                    catch
                    {
                        // Ignore redirect inspection errors
                    }

                    var assetType = response.Headers.TryGetValue("content-type", out var typeStr) ? typeStr : (response.Request.ResourceType ?? "Other");

                    var isSuccess = response.Status >= 200 && response.Status < 400;
                    assetLogs.Add(new AssetLog
                    {
                        Url = response.Url,
                        Status = response.Status,
                        StatusString = isSuccess ? "loaded" : "failed",
                        Reason = isSuccess ? null : response.Status.ToString(),
                        Type = assetType,
                        DurationMs = durationMs,
                        SizeBytes = sizeBytes
                    });
                };

                page.RequestFailed += (sender, req) =>
                {
                    var end = DateTime.UtcNow;
                    var start = requestStartTimes.TryGetValue(req, out var s) ? s : end;
                    var durationMs = (long)(end - start).TotalMilliseconds;

                    assetLogs.Add(new AssetLog
                    {
                        Url = req.Url,
                        Status = 0,
                        StatusString = "failed",
                        Reason = req.Failure ?? "Aborted",
                        Type = req.ResourceType ?? "Failed",
                        DurationMs = durationMs,
                        SizeBytes = 0
                    });
                };

                // Capture Console and Page errors
                page.Console += (sender, msg) =>
                {
                    var text = $"[{msg.Type}] {msg.Text}";
                    if (msg.Type == "error")
                    {
                        command.Diagnostics.JsErrors.Add(text);
                    }
                    else if (msg.Type == "warning")
                    {
                        command.Diagnostics.ConsoleWarnings.Add(text);
                    }
                };

                page.PageError += (sender, error) =>
                {
                    command.Diagnostics.JsErrors.Add($"[Unhandled Error] {error}");
                };

                // Register local typography interceptions
                await _typographyEngine.InterceptFontRequestsAsync(page);

                // Sub-resources blocked by the SSRF guard. Aborting the route alone left
                // the caller with nothing but a generic Chromium "net::ERR_FAILED" in
                // jsErrors — indistinguishable from a 404 or a timeout, and with no URL.
                // A security control that drops content silently is the exact failure this
                // engine's diagnostics exist to prevent, so blocked URLs are collected and
                // reported explicitly. Concurrent because route handlers run in parallel.
                var ssrfBlockedResources = new System.Collections.Concurrent.ConcurrentBag<string>();

                // Fetches a resource ourselves over a connection pinned to a
                // pre-validated, non-private IP, then hands the result back to
                // Chromium via route.FulfillAsync — instead of validating an IP and
                // then letting Chromium reconnect independently (see PinnedIpOptionKey
                // for why that matters). Each redirect hop is re-validated the same way.
                async Task FetchAndFulfillPinnedAsync(IRoute route, Uri initialUrl)
                {
                    var currentUrl = initialUrl;
                    const int maxRedirects = 5;

                    for (int redirectHop = 0; redirectHop <= maxRedirects; redirectHop++)
                    {
                        var host = currentUrl.DnsSafeHost;
                        var ips = await GetHostAddressesWithCacheAndTimeoutAsync(host, timeoutCts.Token);
                        if (ips.Length == 0)
                        {
                            _logger.LogWarning("SSRF Defense: DNS resolution failed or timed out for host {Host}. Request to {Url} aborted.", host, currentUrl);
                            ssrfBlockedThisAttempt = true;
                            ssrfBlockedResources.Add($"{currentUrl} (DNS resolution failed or timed out)");
                            await route.AbortAsync();
                            return;
                        }

                        IPAddress? safeIp = null;
                        foreach (var ip in ips)
                        {
                            if (IsIpSafe(ip)) { safeIp = ip; break; }
                            _logger.LogWarning("SSRF Defense blocked request to {Url} (resolved: {Ip})", currentUrl, ip);
                        }
                        if (safeIp == null)
                        {
                            ssrfBlockedThisAttempt = true;
                            ssrfBlockedResources.Add($"{currentUrl} (resolves to a private, loopback or otherwise disallowed address)");
                            await route.AbortAsync();
                            return;
                        }

                        using var requestMessage = new HttpRequestMessage(new HttpMethod(route.Request.Method), currentUrl);
                        requestMessage.Options.Set(PinnedIpOptionKey, safeIp);

                        foreach (var header in route.Request.Headers)
                        {
                            if (IsRestrictedRequestHeader(header.Key)) continue;
                            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }

                        var postData = route.Request.PostDataBuffer;
                        if (postData is { Length: > 0 })
                        {
                            requestMessage.Content = new ByteArrayContent(postData);
                            if (route.Request.Headers.TryGetValue("content-type", out var contentType))
                            {
                                requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                            }
                        }

                        HttpResponseMessage responseMessage;
                        try
                        {
                            responseMessage = await PinnedResourceHttpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "SSRF-safe fetch failed for {Url}.", currentUrl);
                            ssrfBlockedThisAttempt = true;
                            await route.AbortAsync();
                            return;
                        }

                        using (responseMessage)
                        {
                            if (IsRedirect(responseMessage.StatusCode))
                            {
                                var location = responseMessage.Headers.Location;
                                if (location == null)
                                {
                                    ssrfBlockedThisAttempt = true;
                                    await route.AbortAsync();
                                    return;
                                }
                                currentUrl = location.IsAbsoluteUri ? location : new Uri(currentUrl, location);
                                continue; // re-validate the redirect target from scratch, next loop iteration
                            }

                            var bodyStream = await responseMessage.Content.ReadAsStreamAsync(timeoutCts.Token);
                            using var bodyBuffer = new MemoryStream();
                            var chunk = new byte[81920];
                            int bytesRead;
                            while ((bytesRead = await bodyStream.ReadAsync(chunk, timeoutCts.Token)) > 0)
                            {
                                var runningTotal = Interlocked.Add(ref totalDownloadedBytes, bytesRead);
                                if (runningTotal > maxAssetBytes)
                                {
                                    _logger.LogWarning("Job exceeded asset download limit of {Max}MB while fetching {Url}", planConfig.MaxAssetDownloadMb, currentUrl);
                                    ssrfBlockedThisAttempt = true;
                                    await route.AbortAsync();
                                    return;
                                }
                                bodyBuffer.Write(chunk, 0, bytesRead);
                            }

                            var responseHeaders = new List<KeyValuePair<string, string>>();
                            foreach (var h in responseMessage.Headers)
                            {
                                responseHeaders.Add(new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value)));
                            }
                            foreach (var h in responseMessage.Content.Headers)
                            {
                                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                                responseHeaders.Add(new KeyValuePair<string, string>(h.Key, string.Join(",", h.Value)));
                            }

                            await route.FulfillAsync(new RouteFulfillOptions
                            {
                                Status = (int)responseMessage.StatusCode,
                                Headers = responseHeaders,
                                BodyBytes = bodyBuffer.ToArray()
                            });
                            return;
                        }
                    }

                    _logger.LogWarning("SSRF Defense: redirect chain for {Url} exceeded {Max} hops. Aborting.", initialUrl, maxRedirects);
                    ssrfBlockedThisAttempt = true;
                    await route.AbortAsync();
                }

                // Route filter for SSRF defense
                await page.RouteAsync("**/*", async route =>
                {
                    try
                    {
                        var url = new Uri(route.Request.Url);

                        // Let specific typography routes handle Google Fonts requests
                        if (url.Host.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
                            url.Host.Contains("fonts.gstatic.com", StringComparison.OrdinalIgnoreCase))
                        {
                            await route.FallbackAsync();
                            return;
                        }

                        if (!url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                            !url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                        {
                            // data:, blob:, about: etc. never leave the browser process — nothing to pin.
                            await route.ContinueAsync();
                            return;
                        }

                        await FetchAndFulfillPinnedAsync(route, url);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SSRF Routing failed for {Url}. Request aborted.", route.Request.Url);
                        try
                        {
                            await route.AbortAsync();
                        }
                        catch
                        {
                            // Ignore failures if context or request has already been closed/aborted
                        }
                    }
                });

                if (isUrlMode)
                {
                    var targetUri = new Uri(command.Url!);

                    if (command.Options.Cookies is { Count: > 0 })
                    {
                        var cookies = new List<Microsoft.Playwright.Cookie>();
                        foreach (var kvp in command.Options.Cookies)
                        {
                            cookies.Add(new Microsoft.Playwright.Cookie { Name = kvp.Key, Value = kvp.Value, Url = targetUri.GetLeftPart(UriPartial.Authority) });
                        }
                        await page.Context.AddCookiesAsync(cookies);
                    }

                    var navHeaders = new List<KeyValuePair<string, string>> { new("X-PdfEngine-Render", "1.0.0") };
                    if (command.Options.ExtraHttpHeaders is { Count: > 0 })
                    {
                        foreach (var kvp in command.Options.ExtraHttpHeaders)
                        {
                            navHeaders.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value));
                        }
                    }
                    await page.SetExtraHTTPHeadersAsync(navHeaders);

                    var navWaitUntil = ResolveWaitUntil(command.Options.WaitUntil);
                    try
                    {
                        // Goes through the same "**/*" route handler registered above —
                        // the top-level navigation is SSRF-checked and pinned exactly
                        // like every subresource request, not a separate, unguarded path.
                        await page.GotoAsync(command.Url!, new PageGotoOptions
                        {
                            Timeout = timeoutMs,
                            WaitUntil = navWaitUntil
                        });
                    }
                    catch (TimeoutException ex)
                    {
                        _logger.LogWarning(ex, "Playwright GotoAsync timed out after {TimeoutMs}ms waiting for {WaitUntil} state for {Url}. Proceeding to render PDF from current page state.", timeoutMs, navWaitUntil, command.Url);
                    }
                }
                else
                {
                    var htmlWithMetadata = InjectMetadataTags(renderingContext.Html, command.Options);

                    // SetContentAsync reuses the existing about:blank document via
                    // document.write instead of performing a real navigation, so
                    // AddInitScriptAsync never re-fires for HTML-mode renders — measured:
                    // the page still saw the real Date. The determinism script is
                    // therefore also injected as the first thing in the document, where it
                    // provably runs before any author script. The init script above still
                    // covers URL mode, which is a genuine navigation.
                    if (determinismScript != null)
                    {
                        htmlWithMetadata = InjectHeadScript(htmlWithMetadata, determinismScript);
                    }
                    var navWaitUntil = ResolveWaitUntil(command.Options.WaitUntil);
                    try
                    {
                        await page.SetContentAsync(htmlWithMetadata, new PageSetContentOptions
                        {
                            Timeout = timeoutMs,
                            WaitUntil = navWaitUntil
                        });
                    }
                    catch (TimeoutException ex)
                    {
                        _logger.LogWarning(ex, "Playwright SetContentAsync timed out after {TimeoutMs}ms waiting for {WaitUntil} state. Proceeding to render PDF from current page state.", timeoutMs, navWaitUntil);
                    }
                }

                // Remote web fonts (@import, @font-face with a network src — e.g. Google
                // Fonts) are still downloading/parsing after the Load event fires; the
                // CSS Font Loading spec exposes document.fonts.ready specifically for
                // this. Skipping this wait was verified to silently swap every
                // requested typeface for a system fallback (Helvetica/Times/Arial
                // Unicode) with no error anywhere — the PDF just quietly used the
                // wrong fonts. Pagination measures real glyph metrics next, so this
                // must run before Pass 2, not just before the final PdfAsync capture.
                try
                {
                    // document.fonts.ready alone is not sufficient: it only waits for
                    // font loads the browser has already decided to START — and
                    // Chromium matches/triggers a @font-face fetch lazily, tied to
                    // actually painting text that needs it. Verified directly: on a
                    // long (23-page) document, Outfit/Space Grotesk (used by the body
                    // and every heading, so matched immediately) showed status
                    // "loaded", while Noto Sans Arabic/Devanagari/JP — each scoped to
                    // a specific script/class further down the page — were still
                    // "unloaded" at the exact moment fonts.ready resolved, because
                    // Chromium hadn't gotten around to deciding it needed them yet.
                    // Explicitly forcing every declared FontFace to load removes that
                    // dependency on paint timing entirely.
                    var forceLoadTask = page.EvaluateAsync<bool>(
                        "Promise.all(Array.from(document.fonts).map(f => f.load().catch(() => {}))).then(() => document.fonts.ready).then(() => true)");
                    if (await Task.WhenAny(forceLoadTask, Task.Delay(Math.Min(timeoutMs, 8000))) != forceLoadTask)
                    {
                        _logger.LogWarning("Forced web font loading did not complete within the font-wait budget; proceeding with whatever fonts have loaded so far.");
                        command.Diagnostics.Warnings.Add("Render warning: one or more web fonts (e.g. Google Fonts) may not have finished loading before the PDF was captured — check network access to the font host if custom typefaces look wrong.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Waiting for web fonts to load failed; proceeding with whatever fonts have loaded so far.");
                }

                // Extra CSS/JS the caller wants applied after the document loads,
                // regardless of whether it came from HTML or a live Url.
                if (!string.IsNullOrWhiteSpace(command.Options.ExtraCss))
                {
                    await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = command.Options.ExtraCss });
                }
                if (!string.IsNullOrWhiteSpace(command.Options.ExtraJs) && command.Options.AllowScripts)
                {
                    await page.AddScriptTagAsync(new PageAddScriptTagOptions { Content = command.Options.ExtraJs });
                }

                // Charts/canvas/animated content often aren't done drawing at page-load
                // time — printing immediately captures them blank (the standing project
                // defect this exists to fix). Both waits run before pagination measures
                // layout, since an unsettled chart can report a 0-height bounding box.
                if (!string.IsNullOrWhiteSpace(command.Options.WaitForSelector))
                {
                    try
                    {
                        await page.WaitForSelectorAsync(command.Options.WaitForSelector, new PageWaitForSelectorOptions
                        {
                            Timeout = timeoutMs
                        });
                    }
                    catch (TimeoutException ex)
                    {
                        _logger.LogWarning(ex, "WaitForSelector '{Selector}' timed out after {TimeoutMs}ms. Proceeding to render PDF from current page state.", command.Options.WaitForSelector, timeoutMs);
                        command.Diagnostics.Warnings.Add($"Render warning: WaitForSelector '{command.Options.WaitForSelector}' did not appear within {timeoutMs}ms; rendering proceeded anyway.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(command.Options.WaitForFunction))
                {
                    try
                    {
                        await page.WaitForFunctionAsync(command.Options.WaitForFunction, new PageWaitForFunctionOptions
                        {
                            Timeout = timeoutMs
                        });
                    }
                    catch (TimeoutException ex)
                    {
                        _logger.LogWarning(ex, "WaitForFunction '{Expression}' did not become truthy within {TimeoutMs}ms. Proceeding to render PDF from current page state.", command.Options.WaitForFunction, timeoutMs);
                        command.Diagnostics.Warnings.Add($"Render warning: WaitForFunction '{command.Options.WaitForFunction}' did not become truthy within {timeoutMs}ms; rendering proceeded anyway.");
                    }
                }

                if (command.Options.RenderDelayMs > 0)
                {
                    await page.WaitForTimeoutAsync(command.Options.RenderDelayMs);
                }

                // Pass 2: Execute dynamic page balancing analysis using live page layout
                // context — skipped entirely in FullHeight mode, where the point is one
                // continuous page with no forced breaks at all; otherwise Pass 2 still
                // measures against the standard single-page target height and forces a
                // break partway through what's meant to be one seamless page.
                if (!command.Options.FullHeight)
                {
                    renderingContext.Page = page;
                    await _paginationPlanner.ExecuteAsync(renderingContext, timeoutCts.Token);

                    // T1-8: capture the page floats while the browser still has them laid
                    // out, then take them out of the flow — so the very first PDF, which
                    // every placement measurement is taken against, already excludes them.
                    if (renderingContext.Plan.PageFloats.Count > 0)
                    {
                        try
                        {
                            await CapturePageFloatsAsync(page, renderingContext.Plan, command.Diagnostics.Warnings);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Page float capture failed; floats are left where they were authored.");
                            renderingContext.Plan.PageFloats.Clear();
                            command.Diagnostics.Warnings.Add(
                                "Page float warning: floated elements could not be captured, so they were left exactly where they were authored, mid-flow. No content is missing.");
                        }
                    }
                }

                // Inject Print Sizing and Repeating headers directives
                await page.AddStyleTagAsync(new PageAddStyleTagOptions
                {
                    Content = @"
                        @media print {
                            thead { display: table-header-group !important; }
                            tbody { display: table-row-group !important; }
                            tfoot { display: table-footer-group !important; }
                            tr { page-break-inside: avoid !important; }
                            img { page-break-inside: avoid !important; }
                            ul, ol, table { page-break-inside: avoid !important; }
                        }
                    "
                });

                // Apply Development Watermark if test key or dev environment
                if (command.ApiKey?.KeyPrefix?.StartsWith("pk_test_") == true || 
                    command.ApiKey?.Environment?.Equals("Development", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await page.AddStyleTagAsync(new PageAddStyleTagOptions
                    {
                        Content = @"
                            body::after {
                                content: 'TEST ENVIRONMENT / PDFENGINE';
                                position: fixed;
                                top: 50%;
                                left: 50%;
                                transform: translate(-50%, -50%) rotate(-45deg);
                                font-size: 55px;
                                color: rgba(220, 38, 38, 0.12) !important;
                                font-weight: 900;
                                pointer-events: none;
                                z-index: 999999;
                                font-family: 'Helvetica Neue', Helvetica, Arial, sans-serif;
                            }"
                    });
                }

                // DOM Layout & Overflow Diagnostics
                try
                {
                    var domWarnings = await page.EvaluateAsync<string[]>(@"() => {
                        const list = [];
                        const docScrollWidth = document.documentElement.scrollWidth;
                        const all = document.querySelectorAll('*');
                        const seen = new Set();
                        for (const el of all) {
                            if (el === document.body || el === document.documentElement) continue;
                            const rect = el.getBoundingClientRect();
                            // Only flag elements that actually overflow the document (cause horizontal scroll)
                            if (rect.right > docScrollWidth + 2) {
                                const sel = el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + (typeof el.className === 'string' && el.className ? '.' + el.className.split(' ').filter(Boolean).slice(0,2).join('.') : '');
                                const key = sel + Math.round(rect.width);
                                if (!seen.has(key)) {
                                    seen.add(key);
                                    list.push(`Overflow Warning: Element <${sel}> (${Math.round(rect.width)}px) overflows the document width (${Math.round(docScrollWidth)}px). This may cause content clipping in the PDF.`);
                                }
                            }
                            if (list.length >= 5) break; // Cap warnings to prevent score collapse
                        }
                        return list;
                    }");
                    
                    if (domWarnings != null)
                    {
                        foreach (var w in domWarnings)
                        {
                            command.Diagnostics.Warnings.Add(w);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to run DOM layout bounds check.");
                }

                // Capture current layout screenshot for visual regression comparison
                byte[]? currentScreenshot = null;
                if (command.Options.CaptureScreenshot || !string.IsNullOrEmpty(command.Options.ReferenceScreenshotBase64))
                {
                    try
                    {
                        currentScreenshot = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, FullPage = true });
                    }
                    catch (Exception ssEx)
                    {
                        _logger.LogWarning(ssEx, "Failed to capture page layout screenshot for visual regression.");
                    }

                    if (currentScreenshot != null && !string.IsNullOrEmpty(command.Options.ReferenceScreenshotBase64))
                    {
                        try
                        {
                            var refBytes = Convert.FromBase64String(command.Options.ReferenceScreenshotBase64);
                            command.Diagnostics.VisualDrift = ComputeVisualDrift(currentScreenshot, refBytes);
                        }
                        catch (Exception driftEx)
                        {
                            _logger.LogWarning(driftEx, "Failed to calculate visual layout drift percentage.");
                            command.Diagnostics.VisualDrift = 100.0;
                        }
                    }

                    if (currentScreenshot != null)
                    {
                        command.Diagnostics.ScreenshotBase64 = Convert.ToBase64String(currentScreenshot);
                    }
                }

                // Blank Page Detection
                try
                {
                    var pageStatsJson = await page.EvaluateAsync<System.Text.Json.JsonElement>(@"() => {
                        const pageHeight = 1122; // A4 height at 96 DPI
                        const scrollHeight = document.documentElement.scrollHeight;
                        const numPages = Math.ceil(scrollHeight / pageHeight);
                        
                        const stats = [];
                        for (let i = 0; i < numPages; i++) {
                            stats.push({ page: i + 1, textNodes: 0, images: 0, elements: 0 });
                        }
                        
                        // Traverse text nodes
                        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
                        let node;
                        while (node = walker.nextNode()) {
                            const text = node.nodeValue.trim();
                            if (text.length > 0 && node.parentElement) {
                                const rect = node.parentElement.getBoundingClientRect();
                                const top = rect.top + window.scrollY;
                                const pageIdx = Math.min(numPages - 1, Math.max(0, Math.floor(top / pageHeight)));
                                stats[pageIdx].textNodes++;
                            }
                        }
                        
                        // Traverse images
                        const imgs = document.querySelectorAll('img, svg, picture, canvas');
                        for (const el of imgs) {
                            const rect = el.getBoundingClientRect();
                            if (rect.width > 0 && rect.height > 0) {
                                const top = rect.top + window.scrollY;
                                const pageIdx = Math.min(numPages - 1, Math.max(0, Math.floor(top / pageHeight)));
                                stats[pageIdx].images++;
                            }
                        }

                        // Traverse elements
                        const all = document.querySelectorAll('*');
                        for (const el of all) {
                            if (el === document.body || el === document.documentElement) continue;
                            const rect = el.getBoundingClientRect();
                            if (rect.width > 0 && rect.height > 0) {
                                const top = rect.top + window.scrollY;
                                const pageIdx = Math.min(numPages - 1, Math.max(0, Math.floor(top / pageHeight)));
                                stats[pageIdx].elements++;
                            }
                        }
                        
                        return stats;
                    }");

                    if (pageStatsJson.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in pageStatsJson.EnumerateArray())
                        {
                            command.Diagnostics.BlankPagesDetail.Add(new PageDiagnostics
                            {
                                Page = item.GetProperty("page").GetInt32(),
                                TextNodes = item.GetProperty("textNodes").GetInt32(),
                                Images = item.GetProperty("images").GetInt32(),
                                Elements = item.GetProperty("elements").GetInt32()
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to run blank page detection check.");
                }

                // Layout clipping and positioned element bounds check
                try
                {
                    var layoutWarnings = await page.EvaluateAsync<List<string>>(@"() => {
                        const warnings = [];
                        const getElId = (el) => {
                            let name = el.tagName.toLowerCase();
                            if (el.id) name += '#' + el.id;
                            if (el.className) name += '.' + Array.from(el.classList).join('.');
                            return name;
                        };

                        const allElements = document.querySelectorAll('*');
                        for (const el of allElements) {
                            if (el.clientWidth === 0 || el.clientHeight === 0) continue;
                            const style = window.getComputedStyle(el);
                            
                            // Check overflow hidden clipping
                            if (style.overflow === 'hidden' || style.overflowY === 'hidden') {
                                if (el.scrollHeight > el.clientHeight + 2) {
                                    warnings.push(`Layout warning: Element <${getElId(el)}> has overflow: hidden but its content is clipped vertically (scrollHeight ${el.scrollHeight}px > clientHeight ${el.clientHeight}px).`);
                                }
                            }
                            if (style.overflow === 'hidden' || style.overflowX === 'hidden') {
                                if (el.scrollWidth > el.clientWidth + 2) {
                                    warnings.push(`Layout warning: Element <${getElId(el)}> has overflow: hidden but its content is clipped horizontally (scrollWidth ${el.scrollWidth}px > clientWidth ${el.clientWidth}px).`);
                                }
                            }

                            // Check absolute positioning bounds
                            if (style.position === 'absolute' || style.position === 'fixed') {
                                const rect = el.getBoundingClientRect();
                                const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 800;
                                const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 1200;
                                if (rect.width > 0 && rect.height > 0) {
                                    if (rect.right < 0 || rect.bottom < 0 || rect.left > viewportWidth || rect.top > viewportHeight) {
                                        warnings.push(`Layout warning: Positioned element <${getElId(el)}> is completely outside the print viewport bounds (x: ${rect.left}, y: ${rect.top}, size: ${rect.width}x${rect.height}).`);
                                    }
                                }
                            }
                        }
                        return warnings;
                    }");

                    if (layoutWarnings != null)
                    {
                        foreach (var warning in layoutWarnings)
                        {
                            command.Diagnostics.Warnings.Add(warning);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to execute runtime layout checks.");
                }

                // `Format` sets an explicit paper width/height, and Chromium applies that
                // in preference to the document's own `@page { size: ... }` — so setting
                // both meant the CSS size was ALWAYS ignored. Measured before this fix:
                // `@page{size:A5 landscape}`, `@page{size:A5}` and `@page{size:210mm
                // 148mm}` all produced identical A4 output, while `@page{margin}` was
                // honored — proving the rule reached Chromium and only the size was being
                // overridden. Format is therefore dropped only when the author actually
                // declared a page size, which leaves the default geometry untouched for
                // the documents (the majority) that declare none.
                var authorDeclaresPageSize = command.Options.PreferCSSPageSize
                    && DeclaresCssPageSize(renderingContext.Html);

                var pdfOptions = new PagePdfOptions
                {
                    Format = authorDeclaresPageSize ? null : command.Options.PageSize,
                    Landscape = command.Options.Landscape,
                    Scale = (float)command.Options.Scale,
                    // Honors the document's own `@page { size: ... }` CSS when present;
                    // falls back to Format/Landscape otherwise. Without this, `@page`
                    // rules are silently ignored and every PDF is forced to a fixed box
                    // regardless of what the HTML actually asks for.
                    PreferCSSPageSize = command.Options.PreferCSSPageSize,
                    PrintBackground = command.Options.PrintBackground,
                    DisplayHeaderFooter = command.Options.DisplayHeaderFooter,
                    HeaderTemplate = command.Options.HeaderTemplate,
                    FooterTemplate = command.Options.FooterTemplate,
                    Margin = new Margin
                    {
                        Top = command.Options.MarginTop ?? "0px",
                        Bottom = command.Options.MarginBottom ?? "0px",
                        Left = command.Options.MarginLeft ?? "0px",
                        Right = command.Options.MarginRight ?? "0px"
                    }
                };

                if (!string.IsNullOrWhiteSpace(command.Options.PageRanges))
                {
                    pdfOptions.PageRanges = command.Options.PageRanges;
                }

                // Chromium's native structure-tree export — verified by direct
                // inspection to produce a real /StructTreeRoot with correct H1-H6,
                // P, Table (with TH/TD scope+headers), Figure+Alt, and List/ListItem
                // tagging from ordinary semantic HTML, and to survive the
                // PdfSharpCore post-process pass (metadata/watermark/encryption)
                // intact. Outline is native-generated from that same structure tree
                // when both are requested, so the custom heading-tracked outline
                // path below is skipped for tagged output — running both would
                // produce two competing bookmark trees.
                if (command.Options.GenerateTaggedPdf)
                {
                    pdfOptions.Tagged = true;
                    pdfOptions.Outline = command.Options.GenerateOutlineFromHeadings;
                }

                // T2-6: bleed has to exist at RENDER time. Enlarging the sheet afterwards
                // would frame the artwork in white, which is the opposite of bleed — the
                // point is that ink runs PAST the cut line. The page is therefore rendered
                // at trim plus bleed on every side, and the finished size is recorded as
                // the TrimBox once the render is done.
                if (command.Options.BleedMm > 0 && !command.Options.FullHeight)
                {
                    var bleedPx = MmToPt(command.Options.BleedMm) / 0.75;
                    var trimWidthPx = PaginationPlanner.ComputePageWidthPx(command.Options);
                    // The printable height plus its margins is the full sheet height, taken
                    // from the same size table the width comes from rather than re-derived.
                    var trimHeightPx = PaginationPlanner.ComputePrintableHeightPx(command.Options)
                        + PaginationPlanner.ParseCssSizeToPx(command.Options.MarginTop)
                        + PaginationPlanner.ParseCssSizeToPx(command.Options.MarginBottom);

                    pdfOptions.Format = null;
                    pdfOptions.PreferCSSPageSize = false;
                    // Landscape is cleared because the width and height below ALREADY
                    // encode the orientation. Leaving it set makes Chromium swap them back,
                    // which silently produced a portrait trim box for every landscape job —
                    // measured, A4 landscape with bleed came out 210x297 instead of 297x210.
                    pdfOptions.Landscape = false;
                    pdfOptions.Width = $"{trimWidthPx + bleedPx * 2}px";
                    pdfOptions.Height = $"{trimHeightPx + bleedPx * 2}px";
                }

                if (command.Options.FullHeight)
                {
                    try
                    {
                        var scrollHeightPx = await page.EvaluateAsync<double>("() => document.documentElement.scrollHeight");
                        var marginTopPx = PaginationPlanner.ParseCssSizeToPx(command.Options.MarginTop);
                        var marginBottomPx = PaginationPlanner.ParseCssSizeToPx(command.Options.MarginBottom);

                        // Width/Height replace Format entirely (Playwright doesn't allow
                        // combining them) — Margin still applies on top of both, same as
                        // it does with Format.
                        pdfOptions.Format = null;
                        pdfOptions.PreferCSSPageSize = false; // an @page size would otherwise override our explicit height
                        pdfOptions.Width = $"{PaginationPlanner.ComputePageWidthPx(command.Options)}px";
                        pdfOptions.Height = $"{scrollHeightPx + marginTopPx + marginBottomPx}px";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "FullHeight mode failed to measure document height; falling back to normal paginated output.");
                    }
                }

                // T1-7: named pages. Chromium silently ignores `page: <name>` — verified,
                // a cover declared A4 landscape with 50mm margins came out identical to the
                // body pages — and page geometry changes LAYOUT, so it cannot be corrected
                // after the render the way a running header can. Each run of consecutive
                // content sharing a page name is rendered on its own paper and the parts are
                // stitched. Every later stage that needs a full document renders through
                // this, so page numbers, cross-references and footnote placement are all
                // resolved against the STITCHED document rather than against one part of it.
                var pageOverrides = new PageOverrideState();
                var pageRuns = renderingContext.Plan.PageRuns;
                var baseFormat = pdfOptions.Format;
                var basePreferCss = pdfOptions.PreferCSSPageSize;

                async Task<byte[]> RenderDocumentAsync()
                {
                    if (pageRuns.Count <= 1)
                    {
                        return await page.PdfAsync(pdfOptions);
                    }

                    var parts = new List<byte[]>(pageRuns.Count);
                    foreach (var run in pageRuns)
                    {
                        var named = run.Name.Length > 0
                            && renderingContext.Plan.NamedPages.TryGetValue(run.Name, out var definition)
                            && definition.ChangesGeometry
                            && (definition.PageSize != null || definition.Width != null || definition.Landscape.HasValue);

                        // A run that declares its own paper hands sizing entirely to CSS:
                        // an explicit Format is applied in preference to `@page { size }`,
                        // so leaving it set would silently override the named page.
                        pdfOptions.Format = named ? null : baseFormat;
                        pdfOptions.PreferCSSPageSize = named ? true : basePreferCss;

                        await ApplyPageOverridesAsync(page, renderingContext.Plan, pageOverrides,
                            run.Index, clearPlannerBreaks: false);

                        var part = await page.PdfAsync(pdfOptions);
                        run.PageCount = GetPdfPageCount(part);
                        parts.Add(part);
                    }

                    pdfOptions.Format = baseFormat;
                    pdfOptions.PreferCSSPageSize = basePreferCss;
                    return MergePdfParts(parts);
                }

                if (pageRuns.Count > 1)
                {
                    // The planner's forced breaks were measured against ONE page geometry.
                    // A run on different paper has a different content height, so they are
                    // stale for the same reason a reservation makes them stale.
                    pageOverrides.PlannerBreaksCleared = true;
                    await ApplyPageOverridesAsync(page, renderingContext.Plan, pageOverrides,
                        null, clearPlannerBreaks: true);
                }

                var pdfBytes = await WithRenderBudgetAsync(
                    RenderDocumentAsync, page, timeoutCts.Token, timeoutMs, "PDF capture");

                // T1-5 footnotes and T1-8 page floats. Runs BEFORE the cross-reference
                // pass, deliberately. Reserving edge space moves whole blocks onto later
                // pages, which would invalidate any page number already resolved; the
                // reverse — a substituted page number nudging the layout — is far smaller,
                // and what is left of it is caught by the overlap check at stamping time.
                if (renderingContext.Plan.Footnotes.Count > 0 || renderingContext.Plan.PageFloats.Count > 0)
                {
                    try
                    {
                        pdfBytes = await ApplyPagedBandReflowAsync(
                            page, pdfBytes, RenderDocumentAsync, renderingContext.Plan,
                            command.Options, pageOverrides, command.Diagnostics.Warnings,
                            timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Edge-band reflow failed; footnotes and page floats are placed against the un-reflowed layout.");
                        command.Diagnostics.Warnings.Add(
                            "Layout warning: the footnote/page-float reflow pass failed, so that content was placed against the original layout and may overlap the body text.");
                    }
                }

                // Second pass for cross-references (GCPM target-counter() equivalent).
                // A page number simply does not exist until the document has been
                // paginated, so the only trustworthy source is the PDF we just made:
                // locate each anchor's text fingerprint in the real rendered pages,
                // substitute the true numbers, and render once more. Prince XML does
                // the same thing (it iterates layout until references stabilise).
                // Cost is one extra render, and only for documents that actually use
                // the feature — a no-op for everything else.
                if (renderingContext.Plan.PageRefRequests.Count > 0)
                {
                    try
                    {
                        var resolved = ResolvePageReferencesFromPdf(pdfBytes, renderingContext.Plan.PageRefRequests, _logger);

                        // Runs even when NOTHING resolved. Gating this on resolved.Count > 0
                        // meant a document whose references all dangle skipped the
                        // substitution entirely and shipped blank gaps where the page
                        // numbers should be — the reader cannot tell a broken reference
                        // from a design choice. Every reference now ends up as a number or
                        // a visible '?'.
                        {
                            // Passed as [[id, page], ...] rather than a dictionary:
                            // Playwright's .NET argument serializer rejects JsonElement
                            // and is unreliable for arbitrary maps, but handles plain
                            // arrays of primitives cleanly.
                            var pairs = resolved.Select(kv => new object[] { kv.Key, kv.Value }).ToArray();
                            await page.EvaluateAsync(
                                @"(pairs) => {
                                    const map = new Map(pairs);
                                    document.querySelectorAll('[data-pdfengine-pageref]').forEach(el => {
                                        const p = map.get(el.getAttribute('data-pdfengine-pageref'));
                                        el.textContent = (p === undefined || p === null) ? '?' : String(p);
                                    });
                                }", pairs);

                            pdfBytes = await WithRenderBudgetAsync(
                                RenderDocumentAsync, page, timeoutCts.Token, timeoutMs, "PDF re-capture");
                        }

                        var unresolved = renderingContext.Plan.PageRefRequests
                            .Where(r => !resolved.ContainsKey(r.Id)).Select(r => r.Id).ToList();
                        if (unresolved.Count > 0)
                        {
                            command.Diagnostics.Warnings.Add(
                                $"Page reference warning: {unresolved.Count} cross-reference target(s) could not be located in the rendered PDF and were left as '?': {string.Join(", ", unresolved.Take(5))}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Page-reference resolution pass failed; page references may be unresolved.");
                        command.Diagnostics.Warnings.Add("Page reference warning: cross-reference resolution failed; references may show '?'.");
                    }
                }

                // T1-5: draw the footnote bands. This has to come after the LAST
                // re-render — anything drawn into the PDF is thrown away by the next
                // PdfAsync — and before post-processing, so watermarks, PDF/A metadata
                // and encryption all apply to a document that already contains its
                // footnotes.
                if (renderingContext.Plan.Footnotes.Count > 0 || renderingContext.Plan.PageFloats.Count > 0)
                {
                    try
                    {
                        ResolveLiftedContentPages(pdfBytes, renderingContext.Plan, _logger,
                            command.Diagnostics.Warnings, reportUnresolved: false);

                        // Floats first: the footnote stamper measures the free space left
                        // above the bottom margin, and a bottom float drawn afterwards
                        // would not be counted in that measurement.
                        pdfBytes = StampPageFloats(pdfBytes, renderingContext.Plan, command.Options,
                            _logger, command.Diagnostics.Warnings);
                        pdfBytes = StampFootnotes(pdfBytes, renderingContext.Plan, command.Options,
                            _logger, command.Diagnostics.Warnings);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Footnote/page-float stamping failed; the document is returned without that content.");
                        command.Diagnostics.Warnings.Add(
                            "Layout warning: the footnote text and/or floated content could NOT be drawn, so this document is missing it entirely — any call markers in the body have no matching notes.");
                    }
                }

                // T1-1: running headers/footers. The page each `string-set` assignment
                // landed on is resolved from the REAL rendered PDF, using the same
                // fingerprint matcher as cross-references — a running header naming the
                // wrong chapter is exactly as wrong as a ToC naming the wrong page, and
                // DOM-geometry estimation was already proven wrong twice for those.
                //
                // Drawn BEFORE post-processing, alongside the footnote and page-float
                // bands. It used to run after, and that silently cost the document its
                // headers whenever a password was set: post-processing finishes by
                // encrypting, and nothing can reopen an encrypted PDF to draw on it.
                // Measured — running headers plus AES-256 produced an encrypted document
                // with no headers at all. Two separately-verified features that did not
                // compose.
                if (renderingContext.Plan.MarginBoxes.Count > 0)
                {
                    try
                    {
                        var assignments = renderingContext.Plan.StringSetAssignments;
                        if (assignments.Count > 0)
                        {
                            var requests = assignments.Select((a, i) => new PageRefRequest
                            {
                                Id = $"__stringset_{i}",
                                Fingerprint = a.Fingerprint,
                                ShortFingerprint = a.ShortFingerprint
                            }).ToList();

                            var pages = ResolvePageReferencesFromPdf(
                                pdfBytes, requests, _logger, preferLastOnFallback: false);
                            for (var i = 0; i < assignments.Count; i++)
                            {
                                assignments[i].Page = pages.TryGetValue($"__stringset_{i}", out var p) ? p : 0;
                            }
                        }

                        pdfBytes = StampMarginBoxes(
                            pdfBytes, renderingContext.Plan, command.Options,
                            _logger, command.Diagnostics.Warnings);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Running header/footer stamping failed; the document is returned without margin boxes.");
                        command.Diagnostics.Warnings.Add(
                            "Layout warning: running headers/footers could not be applied; the document was returned without them.");
                    }
                }

                // What the engine draws after Chromium has finished is raw page content
                // that is not in the structure tree, and PDF/UA-1 clause 7.1 requires
                // every mark to be either tagged or flagged as an /Artifact.
                //
                // Running headers, folios and watermarks ARE artifacts — page furniture
                // that repeats and belongs to no sentence — and are now declared as such,
                // so they compose with tagged output. Measured with veraPDF 1.30.2 on
                // 2026-08-20: tagged alone 1492/0, tagged + running header 1560/0,
                // tagged + watermark 1530/0, tagged + both 1598/0.
                //
                // Footnotes and page floats are NOT artifacts. A footnote is content the
                // reader is meant to read, and marking it an artifact would satisfy the
                // validator while hiding the footnote from the screen reader it exists
                // for — a worse document that passes a better test. The band is therefore
                // a real /Note structure element instead, joined to the tree with its own
                // ParentTree entry: measured 1647/0, conformant. Page floats are still
                // untagged and are still reported.
                if (command.Options.GenerateTaggedPdf)
                {
                    var untagged = new List<string>();
                    // Footnotes are NOT listed: the band is a real /Note in the structure
                    // tree now, so tagged + footnote is conformant — measured 1647/0.
                    if (renderingContext.Plan.PageFloats.Count > 0) untagged.Add("page floats");
                    if (command.Options.DisplayHeaderFooter
                        && (!string.IsNullOrEmpty(command.Options.HeaderTemplate)
                            || !string.IsNullOrEmpty(command.Options.FooterTemplate)))
                    {
                        // Chromium's own header/footer, unlike the engine's, is drawn
                        // inside Chromium's content stream with no artifact marking, and
                        // there is no hook to change that. The engine's `@page` margin
                        // boxes are the conformant way to get the same result.
                        untagged.Add("Chromium's headerTemplate/footerTemplate (use @page margin boxes instead, which ARE marked as artifacts)");
                    }

                    if (untagged.Count > 0)
                    {
                        command.Diagnostics.Warnings.Add(
                            $"Accessibility warning: this document requests tagged (PDF/UA) output AND uses {string.Join(", ", untagged)}, which is not in the structure tree, so the document will NOT pass PDF/UA-1 validation (measured with veraPDF 1.30.2: Chromium's header/footer costs 3 checks of clause 7.1). Running headers via @page margin boxes, watermarks and footnotes are unaffected — headers and watermarks are declared as artifacts, and a footnote band is a real /Note structure element.");
                    }
                }

                // The planner counts outline pages during one continuous pass over the
                // DOM, which stops being true once the document is rendered in parts.
                if (pageRuns.Count > 1 && command.Options.GenerateOutlineFromHeadings)
                {
                    RelocateOutlineForStitchedDocument(pdfBytes, renderingContext.Plan, _logger);
                }

                // Real /Info metadata, bookmarks/outline (from the pagination planner's
                // own heading-to-page tracking), watermark, and encryption — one
                // consolidated post-process pass, each independently gated.
                pdfBytes = ApplyPdfPostProcessing(pdfBytes, command.Options, renderingContext.Plan.HeadingOutline, _logger, command.Diagnostics.Warnings);

                if (!ssrfBlockedResources.IsEmpty)
                {
                    var blocked = ssrfBlockedResources.Distinct().ToList();
                    command.Diagnostics.Warnings.Add(
                        $"Security notice: {blocked.Count} sub-resource(s) were BLOCKED by the "
                        + "SSRF guard and are missing from this document: "
                        + string.Join("; ", blocked.Take(5))
                        + (blocked.Count > 5 ? $" (+{blocked.Count - 5} more)" : string.Empty));
                }

                // RB-2: give right-to-left runs a logical-order /ActualText. Not behind an
                // option because a text layer that cannot be copied or searched is a defect,
                // not a preference. It is inherently a no-op for documents with no RTL text
                // — no /ReversedChars run means no rewrite and no extra save.
                pdfBytes = ApplyActualTextToReversedRuns(pdfBytes, _logger, command.Diagnostics.Warnings);

                // T2-6: record the finished size and add crop marks. Before linearization,
                // because it rewrites the whole document.
                if (command.Options.BleedMm > 0 || command.Options.CropMarks)
                {
                    try
                    {
                        // The nominal paper size in points, so the TrimBox is exact rather
                        // than inheriting Chromium's pixel rounding.
                        var nominalWidthPt = PaginationPlanner.ComputePageWidthPx(command.Options) * 0.75;
                        var nominalHeightPt = (PaginationPlanner.ComputePrintableHeightPx(command.Options)
                            + PaginationPlanner.ParseCssSizeToPx(command.Options.MarginTop)
                            + PaginationPlanner.ParseCssSizeToPx(command.Options.MarginBottom)) * 0.75;
                        pdfBytes = ApplyPrintProduction(pdfBytes, command.Options,
                            nominalWidthPt, nominalHeightPt, _logger, command.Diagnostics.Warnings);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Print production pass failed; the document is returned without trim boxes or crop marks.");
                        command.Diagnostics.Warnings.Add(
                            "Print warning: the bleed/crop-mark pass failed, so this document has NO TrimBox and no crop marks. Do not send it to a printer expecting them.");
                    }
                }

                // T2-5: fast web view. After everything that rewrites bytes, and before
                // the signature that seals them.
                if (command.Options.Linearize)
                {
                    try
                    {
                        pdfBytes = ApplyLinearization(pdfBytes, command.Options, _logger, command.Diagnostics.Warnings);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _logger.LogError("Linearization failed: {Message}", ex.Message);
                        return Result<byte[]>.Fail(Error.Validation($"Linearization failed: {ex.Message}"));
                    }
                }

                // T2-2: seal the document. LAST of everything, because a signature covers
                // the file's bytes and anything written afterwards invalidates it — the
                // /ActualText rewrite above would break a signature applied before it.
                if (!string.IsNullOrWhiteSpace(command.Options.SigningCertificateBase64))
                {
                    try
                    {
                        pdfBytes = ApplyDigitalSignature(pdfBytes, command.Options, _logger, command.Diagnostics.Warnings);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // A document that claims to be signed and is not is worse than an
                        // honest failure, so this does NOT degrade to an unsigned file.
                        _logger.LogError("Digital signing failed: {Message}", ex.Message);
                        return Result<byte[]>.Fail(Error.Validation($"Digital signing failed: {ex.Message}"));
                    }
                }

                stopwatch.Stop();

                // Font Embedding Verification
                _typographyEngine.VerifyEmbeddedFonts(pdfBytes, command.Diagnostics.EmbeddedFonts);

                var pageCount = GetPdfPageCount(pdfBytes);
                var allowedPages = command.ApiKey != null ? planConfig.MaxPages : 100;
                if (pageCount > allowedPages)
                {
                    throw new Exception($"PDF page count ({pageCount}) exceeded maximum allowed pages ({allowedPages}) {(command.ApiKey != null ? "on current plan" : "in playground mode")}.");
                }

                // Populate final diagnostics
                command.Diagnostics.Assets.AddRange(assetLogs);
                command.Diagnostics.Pages = pageCount;
                command.Diagnostics.FileSize = pdfBytes.Length;
                command.Diagnostics.DurationMs = stopwatch.ElapsedMilliseconds;

                // Calculate cost: base $0.00010 + CPU time ($0.00005 / 100ms) + storage transfer ($0.000008 / KB)
                double rawCost = 0.00010 + (command.Diagnostics.DurationMs * 0.0000005) + ((command.Diagnostics.FileSize / 1024.0) * 0.000008);
                command.Diagnostics.EstimatedCost = Math.Round(rawCost, 6);

                // Calculate Render Certification Score component categories (0-25 each)
                int layoutWarningsCount = command.Diagnostics.Warnings.Count(w => w.Contains("Layout warning:") || w.Contains("Actionable CSS Suggestion:"));
                command.Diagnostics.LayoutScore = Math.Max(0, 25 - (layoutWarningsCount * 5));

                int failedAssetsCount = command.Diagnostics.Assets.Count(asset => asset.Status == 0 || asset.Status >= 400);
                command.Diagnostics.AssetScore = Math.Max(0, 25 - (failedAssetsCount * 10));

                command.Diagnostics.PerformanceScore = Math.Max(0, 25 - (int)(command.Diagnostics.DurationMs / 500) * 5);

                var blankPages = command.Diagnostics.BlankPagesDetail
                    .Where(p => p.TextNodes == 0 && p.Images == 0)
                    .Select(p => p.Page)
                    .ToList();
                int consistencyDeductions = (command.Diagnostics.JsErrors.Count * 15) + (command.Diagnostics.ConsoleWarnings.Count * 2) + (blankPages.Count * 15);
                command.Diagnostics.ConsistencyScore = Math.Max(0, 25 - consistencyDeductions);

                command.Diagnostics.RenderScore = command.Diagnostics.LayoutScore + command.Diagnostics.AssetScore + command.Diagnostics.PerformanceScore + command.Diagnostics.ConsistencyScore;

                var issues = new List<string>();
                if (command.Diagnostics.JsErrors.Count > 0)
                {
                    issues.Add($"{command.Diagnostics.JsErrors.Count} JavaScript execution error(s) detected.");
                }
                if (command.Diagnostics.ConsoleWarnings.Count > 0)
                {
                    issues.Add($"{command.Diagnostics.ConsoleWarnings.Count} console warning(s) logged.");
                }
                if (command.Diagnostics.Warnings.Count > 0)
                {
                    foreach (var w in command.Diagnostics.Warnings)
                    {
                        issues.Add(w);
                    }
                }
                if (failedAssetsCount > 0)
                {
                    issues.Add($"{failedAssetsCount} asset resource(s) failed to load.");
                }
                if (blankPages.Count > 0)
                {
                    issues.Add($"Blank page(s) detected at indices: {string.Join(", ", blankPages)}");
                }

                command.Diagnostics.Issues.AddRange(issues);

                // Record Prometheus metrics
                PdfGeneratedCounter.WithLabels(tenantName, "success").Inc();
                PdfDurationHistogram.WithLabels(tenantName).Observe(stopwatch.Elapsed.TotalSeconds);
                
                return Result<byte[]>.Success(pdfBytes);
            }
            catch (TimeoutException ex)
            {
                // The render budget expired. Retrying costs the caller another full budget
                // and ends the same way — the document is expensive, not unlucky — so this
                // returns immediately and as 408, not as a 500. A 500 would say the server
                // is broken for a document the server correctly declined to spend more
                // time on.
                _logger.LogWarning(ex, "Render budget exceeded on attempt {Attempt}; not retrying.", attempt);
                return Result<byte[]>.Fail(Error.RenderTimeout(ex.Message));
            }
            catch (Exception ex)
            {
                // A blocked navigation (SSRF, byte limit, malformed redirect, etc.)
                // throws a PlaywrightException the same way an actual browser crash
                // does — but retrying won't change the outcome, since the destination
                // is still blocked identically every time, and it wastes a full
                // browser-pool recycle for nothing. Confirmed by testing: without the
                // ssrfBlockedThisAttempt check, a single blocked navigation triggered
                // 3 retries and 3 unnecessary recycles before finally failing.
                bool isCrash = !ssrfBlockedThisAttempt && (
                               ex is PlaywrightException ||
                               ex.Message.Contains("browser", StringComparison.OrdinalIgnoreCase) ||
                               ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                               ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase));

                if (isCrash && attempt < 3)
                {
                    _logger.LogWarning(ex, "Playwright browser disconnected or crashed during rendering (attempt {Attempt}/3). Forcing pool recycle and retrying...", attempt);
                    try
                    {
                        await _browserManager.ForceRecycleBrowserAsync();
                    }
                    catch (Exception recycleEx)
                    {
                        _logger.LogError(recycleEx, "Failed to force recycle browser pool in retry block.");
                    }
                    continue; // Loop back and retry rendering
                }

                stopwatch.Stop();
                PdfGeneratedCounter.WithLabels(tenantName, "failure").Inc();
                _logger.LogError(ex, "PDF generation failed for tenant {Tenant} after attempt {Attempt}.", tenantName, attempt);

                if (ssrfBlockedThisAttempt)
                {
                    return Result<byte[]>.Fail(Error.BlockedUrl(
                        "The requested URL (or a resource it references) resolves to a private, loopback, or otherwise disallowed address and was blocked."));
                }

                return Result<byte[]>.Fail(new Error("PDF_GENERATION_FAILED", ex.Message));
            }
            finally
            {
                if (page != null)
                {
                    try { await page.CloseAsync(); } catch { }
                }
                if (tempContext != null)
                {
                    try { await tempContext.CloseAsync(); } catch { }
                }

                if (captureHar && File.Exists(harTempPath))
                {
                    try
                    {
                        command.Diagnostics.HarJson = await File.ReadAllTextAsync(harTempPath);
                        File.Delete(harTempPath);
                    }
                    catch (Exception harEx)
                    {
                        _logger.LogWarning(harEx, "Failed to read or clean up temporary HAR file at {Path}", harTempPath);
                    }
                }
            }
        }

        return Result<byte[]>.Fail(new Error("PDF_GENERATION_FAILED", "PDF generation failed after 3 attempts due to browser failures."));
    }

    private async Task<IPAddress[]> GetHostAddressesWithCacheAndTimeoutAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            return new[] { parsedIp };
        }

        var now = DateTime.UtcNow;

        if (DnsCache.TryGetValue(host, out var cached) && cached.Expiry > now)
        {
            return cached.IPs;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            var ips = await Dns.GetHostAddressesAsync(host, timeoutCts.Token);
            DnsCache[host] = (ips, now.Add(DnsCacheTtl));
            return ips;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS resolution failed or timed out for host {Host}", host);
            var empty = Array.Empty<IPAddress>();
            DnsCache[host] = (empty, now.Add(DnsFailureCacheTtl));
            return empty;
        }
    }

    private static bool IsRestrictedRequestHeader(string name) =>
        name.Equals("host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("content-length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("content-type", StringComparison.OrdinalIgnoreCase); // set explicitly on the content, not the message

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsIpSafe(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
                return false;

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                var bytes16 = ip.GetAddressBytes();

                // Deprecated IPv4-compatible IPv6 (::a.b.c.d, first 96 bits zero) —
                // unwrap and re-check the embedded IPv4 address rather than trusting it.
                bool isIPv4Compatible = true;
                for (int i = 0; i < 12; i++)
                {
                    if (bytes16[i] != 0) { isIPv4Compatible = false; break; }
                }
                if (isIPv4Compatible)
                {
                    ip = new IPAddress(new[] { bytes16[12], bytes16[13], bytes16[14], bytes16[15] });
                }
                else if (bytes16[0] == 0x20 && bytes16[1] == 0x02)
                {
                    // 6to4 (2002::/16) embeds an IPv4 address in the next 32 bits —
                    // unwrap and re-check it instead of trusting the tunnel blindly.
                    ip = new IPAddress(new[] { bytes16[2], bytes16[3], bytes16[4], bytes16[5] });
                }
                else
                {
                    return true;
                }
            }
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();

            if (bytes[0] == 0) return false;                                    // 0.0.0.0/8
            if (bytes[0] == 127) return false;                                  // Loopback 127.0.0.0/8
            if (bytes[0] == 10) return false;                                   // Private 10.0.0.0/8
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false; // Private 172.16.0.0/12
            if (bytes[0] == 192 && bytes[1] == 168) return false;               // Private 192.168.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254) return false;               // Link-local 169.254.0.0/16
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return false; // CGNAT 100.64.0.0/10
            if (bytes[0] >= 224) return false;                                  // Multicast (224.0.0.0/4) + reserved (240.0.0.0/4)
        }

        return true;
    }

    /// <summary>
    /// Combines already-rendered PDFs into one document, in the order supplied —
    /// e.g. a cover page + a generated invoice + terms-and-conditions as a single
    /// deliverable. PdfDocumentOpenMode.Import (not Modify) is intentional: it reads
    /// pages for copying into a new document rather than opening the source for
    /// in-place editing, which is both correct for this operation and meaningfully
    /// faster since it skips content-stream parsing the merge doesn't need.
    /// </summary>
    public Task<Result<byte[]>> MergeAsync(MergePdfCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Files.Count < 2)
        {
            return Task.FromResult(Result<byte[]>.Fail(Error.Validation("Provide at least two PDF files to merge.")));
        }

        try
        {
            using var target = new PdfSharpCore.Pdf.PdfDocument();

            foreach (var fileBase64 in command.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] sourceBytes;
                try
                {
                    sourceBytes = Convert.FromBase64String(fileBase64);
                }
                catch (FormatException)
                {
                    return Task.FromResult(Result<byte[]>.Fail(Error.Validation("One or more files are not valid base64-encoded PDF data.")));
                }

                using var input = new MemoryStream(sourceBytes);
                PdfSharpCore.Pdf.PdfDocument source;
                try
                {
                    source = PdfSharpCore.Pdf.IO.PdfReader.Open(input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MergeAsync: one of the supplied files could not be opened as a PDF.");
                    return Task.FromResult(Result<byte[]>.Fail(Error.Validation("One or more files are not valid PDF documents.")));
                }

                for (int i = 0; i < source.PageCount; i++)
                {
                    target.AddPage(source.Pages[i]);
                }
            }

            using var output = new MemoryStream();
            target.Save(output);
            return Task.FromResult(Result<byte[]>.Success(output.ToArray()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MergeAsync failed for document {DocumentName}.", command.DocumentName);
            return Task.FromResult(Result<byte[]>.Fail(Error.Internal("PDF merge failed: " + ex.Message)));
        }
    }

    /// <summary>Back-compat entry point used by existing metadata tests — delegates to the consolidated post-process pass.</summary>
    internal static byte[] ApplyPdfMetadata(byte[] pdfBytes, RenderingOptions options, ILogger? logger = null)
        => ApplyPdfPostProcessing(pdfBytes, options, headingOutline: null, logger);

    /// <summary>
    /// One PdfSharpCore open/save pass that applies every post-render PDF feature:
    /// real /Info metadata, a bookmarks/outline tree built from the pagination
    /// planner's heading-to-page mapping, a diagonal text watermark, and
    /// owner/user-password encryption with permission flags. All four are optional
    /// and independently gated — this only opens/re-saves the PDF if at least one is
    /// actually requested.
    /// </summary>
    internal static byte[] ApplyPdfPostProcessing(byte[] pdfBytes, RenderingOptions options, List<HeadingOutlineEntry>? headingOutline, ILogger? logger = null, List<string>? diagnosticWarnings = null)
    {
        var needsMetadata = !string.IsNullOrEmpty(options.Title) || !string.IsNullOrEmpty(options.Author) ||
                             !string.IsNullOrEmpty(options.Subject) || !string.IsNullOrEmpty(options.Keywords);
        var needsWatermark = !string.IsNullOrEmpty(options.WatermarkText);
        var needsImageWatermark = !string.IsNullOrEmpty(options.WatermarkImageBase64);
        var needsEncryption = !string.IsNullOrEmpty(options.OwnerPassword) || !string.IsNullOrEmpty(options.UserPassword);
        // The validator rejects PdfaCompliance + encryption together up front (the
        // PDF/A spec forbids encryption outright — this isn't a "degrade gracefully"
        // situation like outline/metadata above), so needsEncryption is never also
        // true here in practice; this flag only exists to skip the PDF/A work
        // entirely when it wasn't requested.
        var needsPdfa = !string.IsNullOrEmpty(options.PdfaCompliance);
        var needsAttachments = options.Attachments is { Count: > 0 };
        var needsFormFields = options.FormFields is { Count: > 0 };
        // A tagged PDF also requires an XMP metadata stream in the catalog, and a
        // PDF/UA identification schema inside it. veraPDF's PDF/UA-1 profile fails
        // clause 7.1 test 8 (missing /Metadata) and clause 5 test 1 (missing
        // pdfuaid) without them, so tagged output runs the same metadata pass even
        // when no PDF/A level was requested.
        var needsXmpForTagging = options.GenerateTaggedPdf && !needsPdfa;
        // When GenerateTaggedPdf is on, Chromium already embedded a native outline
        // (driven by the same GenerateOutlineFromHeadings flag, wired at PdfAsync
        // call time) directly from the structure tree — running the custom
        // heading-tracked ApplyOutline pass on top would produce two competing
        // bookmark trees in the same document.
        var needsOutline = options.GenerateOutlineFromHeadings && headingOutline is { Count: > 0 } && !options.GenerateTaggedPdf;

        // Verified empirically: PdfSharpCore encrypts page content correctly, and
        // outline/bookmark titles read correctly on their own — but combining the two
        // produces corrupted (garbled) outline title strings under standard PDF string
        // decryption, reproduced independently of any particular reader. Rather than
        // ship bookmarks that look broken whenever a password is also set, skip outline
        // generation in that case and say why, instead of silently emitting bad output.
        // Previously outline+encryption had to be skipped: PdfSharpCore garbles outline
        // titles when it encrypts. Encryption now happens in a separate AES-256 pass
        // AFTER PdfSharpCore has finished, so the conflict no longer exists.

        // The same corruption reproduces for /Info dictionary strings (Title/Author/
        // Subject/Keywords) — verified independently of outline, in the simplest
        // possible isolated document, regardless of whether metadata or security
        // settings are applied first. Shipping garbled document properties is worse
        // than omitting them: a reader's file-properties panel showing mojibake
        // looks broken in a way a missing Title does not. Same fail-honest posture
        // as the outline case above, not a silent drop.
        // Same reasoning as the outline case above — /Info corruption was a symptom of
        // PdfSharpCore doing the encryption, which it no longer does.

        if (!needsMetadata && !needsOutline && !needsWatermark && !needsImageWatermark && !needsEncryption && !needsPdfa && !needsXmpForTagging && !needsAttachments && !needsFormFields)
        {
            return pdfBytes;
        }

        try
        {
            using var input = new MemoryStream(pdfBytes);
            using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

            if (needsMetadata)
            {
                if (!string.IsNullOrEmpty(options.Title)) document.Info.Title = options.Title;
                if (!string.IsNullOrEmpty(options.Author)) document.Info.Author = options.Author;
                if (!string.IsNullOrEmpty(options.Subject)) document.Info.Subject = options.Subject;
                if (!string.IsNullOrEmpty(options.Keywords)) document.Info.Keywords = options.Keywords;
            }
            document.Info.Creator = "PdfEngine";

            if (needsOutline)
            {
                ApplyOutline(document, headingOutline!);
            }

            if (needsWatermark)
            {
                ApplyWatermark(document, options.WatermarkText!);
            }

            if (needsImageWatermark)
            {
                ApplyImageWatermark(document, options.WatermarkImageBase64!, options.WatermarkImageOpacity, logger);
            }

            if (needsAttachments)
            {
                ApplyAttachments(document, options, logger, diagnosticWarnings);
            }

            if (needsFormFields)
            {
                ApplyFormFields(document, options, logger, diagnosticWarnings);
            }

            if (needsPdfa || needsXmpForTagging)
            {
                ApplyPdfaCompliance(document, options, xmpOnly: needsXmpForTagging);
            }

            // Runs last, so it also covers fonts embedded by the passes above. Only for
            // documents that will actually be validated: the entry changes nothing about
            // how the file renders, and adding it everywhere would alter bytes for every
            // caller to fix a problem only conformance checking has.
            if (needsPdfa || needsXmpForTagging)
            {
                var patched = AddMissingCidToGidMaps(document);
                if (patched > 0)
                {
                    logger?.LogDebug("Added /CIDToGIDMap to {Count} embedded CIDFontType2 font(s).", patched);
                }
            }

            using var output = new MemoryStream();
            document.Save(output);
            var result = output.ToArray();

            // AES-256 encryption runs last, on the finished bytes, via PDFsharp (RB-1).
            return needsEncryption ? ApplyAes256Encryption(result, options, logger, diagnosticWarnings) : result;
        }
        catch (InvalidOperationException)
        {
            throw; // encryption failure must not silently degrade to an unencrypted PDF
        }
        catch (Exception ex)
        {
            // None of these are rendering-correctness requirements — if the post-process
            // step fails, ship the original render rather than fail the whole job over it.
            logger?.LogWarning(ex, "Failed to apply PDF post-processing (metadata/outline/watermark/PDF-A); returning render without it.");
            return needsEncryption ? ApplyAes256Encryption(pdfBytes, options, logger, diagnosticWarnings) : pdfBytes;
        }
    }

    /// <summary>
    /// Builds a nested bookmarks/outline tree from headings, using the page each
    /// heading landed on according to the pagination planner's own tracking — the
    /// same state that decided actual page breaks, not a second, independently
    /// re-derived estimate. Note this inherits the planner's heuristic accuracy: it's
    /// most precise where the engine's own forced breaks dominate pagination, less so
    /// where content overflows via Chromium's native pagination between forced breaks.
    /// </summary>
    private static void ApplyOutline(PdfSharpCore.Pdf.PdfDocument document, List<HeadingOutlineEntry> headingOutline)
    {
        var stack = new List<(int Level, PdfSharpCore.Pdf.PdfOutline Outline)>();

        foreach (var heading in headingOutline)
        {
            if (document.PageCount == 0) break;
            var pageIndex = Math.Clamp(heading.Page - 1, 0, document.PageCount - 1);
            var page = document.Pages[pageIndex];

            while (stack.Count > 0 && stack[^1].Level >= heading.Level)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            var parentOutlines = stack.Count > 0 ? stack[^1].Outline.Outlines : document.Outlines;
            var outline = parentOutlines.Add(heading.Text, page, true);

            stack.Add((heading.Level, outline));
        }
    }


    /// <summary>
    /// Opens an /Artifact marked-content block on a page. Everything drawn until
    /// <see cref="EndArtifact"/> is declared to be page furniture rather than document
    /// content, which is what PDF/UA-1 clause 7.1 requires of every mark that is not in
    /// the structure tree.
    ///
    /// Only use this for content that genuinely IS furniture — running headers, folios,
    /// watermarks. A footnote is not furniture: marking it an artifact would satisfy the
    /// validator while HIDING the footnote from the screen reader it exists for, which is
    /// a worse document that passes a better test.
    ///
    /// The block spans several content streams. That is legal: ISO 32000-1 7.8.2 defines
    /// a page's /Contents array as a single stream formed by concatenation, so a BDC in
    /// one stream and its EMC in another enclose everything appended between them.
    /// </summary>

    /// <summary>
    /// Enforces the render budget on work Playwright will not cancel for us.
    ///
    /// `SetDefaultTimeout` bounds each Playwright ACTION, and `PdfAsync` is one action that
    /// can take arbitrarily long inside Chromium — an SVG with 200,000 rects renders for
    /// minutes while the timeout that was meant to bound it never fires. Found by
    /// tests/fuzz_gate.py: a 5 MB document, comfortably under every size limit, held a
    /// worker open past 75 seconds. That is a denial of service with a valid Content-Type.
    ///
    /// The page is closed on expiry, which is what actually stops Chromium working — a
    /// cancelled token alone would leave the render running and the worker occupied.
    /// </summary>
    private async Task<T> WithRenderBudgetAsync<T>(
        Func<Task<T>> work, IPage page, CancellationToken budget, int budgetMs, string step)
    {
        var task = work();
        var expiry = Task.Delay(Timeout.Infinite, budget);
        if (await Task.WhenAny(task, expiry) == task)
        {
            return await task;
        }

        _logger.LogWarning("Render exceeded its {BudgetMs}ms budget during {Step}; closing the page.", budgetMs, step);
        try { await page.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Page close after budget expiry failed."); }
        // Observe the abandoned task's exception so it does not surface as unobserved.
        _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        throw new TimeoutException(
            $"Rendering exceeded the {budgetMs / 1000}s budget during {step}. The document is too expensive to render — reduce the number of elements, image size or SVG complexity.");
    }

    private static void BeginArtifact(PdfSharpCore.Pdf.PdfPage page, string artifactType, string? subtype = null)
    {
        var dict = subtype is null
            ? $"<< /Type {artifactType} >>"
            : $"<< /Type {artifactType} /Subtype {subtype} >>";
        AppendRawContent(page, $"/Artifact {dict} BDC\n");
    }

    private static void EndArtifact(PdfSharpCore.Pdf.PdfPage page) => AppendRawContent(page, "EMC\n");

    private static void AppendRawContent(PdfSharpCore.Pdf.PdfPage page, string operators)
    {
        var content = page.Contents.AppendContent();
        content.CreateStream(Encoding.ASCII.GetBytes(operators));
    }

    /// <summary>
    /// Adds the /CIDToGIDMap every embedded CIDFontType2 is required to carry.
    ///
    /// ISO 32000-1 9.7.4 Table 117 makes it required for Type 2 CIDFonts, and PdfSharpCore
    /// omits it. Nothing renders wrong — every viewer defaults to Identity — so it is
    /// invisible until an accessibility audit runs, where it costs PDF/UA clause 7.21.3.2
    /// on every document where the engine embedded a font of its own.
    /// </summary>
    private static int AddMissingCidToGidMaps(PdfSharpCore.Pdf.PdfDocument document)
    {
        var fixedUp = 0;
        foreach (var item in document.Internals.GetAllObjects())
        {
            if (item is not PdfSharpCore.Pdf.PdfDictionary dict) continue;
            if (dict.Elements.GetName("/Subtype") != "/CIDFontType2") continue;
            if (dict.Elements.ContainsKey("/CIDToGIDMap")) continue;
            dict.Elements["/CIDToGIDMap"] = new PdfSharpCore.Pdf.PdfName("/Identity");
            fixedUp++;
        }
        return fixedUp;
    }

    private static void ApplyWatermark(PdfSharpCore.Pdf.PdfDocument document, string watermarkText)
    {
        var font = new PdfSharpCore.Drawing.XFont("Helvetica", 48, PdfSharpCore.Drawing.XFontStyle.Bold);
        var brush = new PdfSharpCore.Drawing.XSolidBrush(PdfSharpCore.Drawing.XColor.FromArgb(60, 200, 30, 30));

        foreach (var page in document.Pages)
        {
            // A watermark is page furniture, not content: a screen reader announcing
            // "DRAFT" in the middle of a sentence is worse than not announcing it.
            BeginArtifact(page, "/Pagination", "/Watermark");
            using (var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page, PdfSharpCore.Drawing.XGraphicsPdfPageOptions.Append))
            {
            gfx.TranslateTransform(page.Width / 2, page.Height / 2);
            gfx.RotateTransform(-45);
            var size = gfx.MeasureString(watermarkText, font);
            gfx.DrawString(watermarkText, font, brush, -size.Width / 2, 0);
            }
            EndArtifact(page);
        }
    }

    private static void ApplyImageWatermark(PdfSharpCore.Pdf.PdfDocument document, string base64Image, double opacity, ILogger? logger)
    {
        try
        {
            var imageBytes = Convert.FromBase64String(base64Image);
            imageBytes = ApplyAlphaToPng(imageBytes, opacity);
            using var image = PdfSharpCore.Drawing.XImage.FromStream(() => new MemoryStream(imageBytes));

            // Size to a third of the page width, centered, preserving aspect ratio.
            foreach (var page in document.Pages)
            {
                BeginArtifact(page, "/Pagination", "/Watermark");
                using (var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(page, PdfSharpCore.Drawing.XGraphicsPdfPageOptions.Append))
                {
                    var targetWidth = page.Width.Point / 3.0;
                    var targetHeight = targetWidth * (image.PixelHeight / (double)image.PixelWidth);
                    var x = (page.Width.Point - targetWidth) / 2;
                    var y = (page.Height.Point - targetHeight) / 2;

                    var state = gfx.Save();
                    gfx.DrawImage(image, x, y, targetWidth, targetHeight);
                    gfx.Restore(state);
                }
                EndArtifact(page);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to apply image watermark; continuing without it.");
        }
    }

    /// <summary>
    /// PdfSharpCore's DrawImage has no opacity parameter, but a PDF image XObject
    /// respects per-pixel alpha the same way it does for any transparent PNG — so
    /// translucency is achieved by pre-multiplying the desired opacity into the
    /// image's own alpha channel before handing it to PdfSharpCore, rather than at
    /// draw time. Verified against the earlier finding that WatermarkText's
    /// translucency (via XColor.FromArgb's alpha channel) works correctly — the same
    /// underlying PDF transparency mechanism, applied here at the raster level instead.
    /// </summary>
    private static byte[] ApplyAlphaToPng(byte[] imageBytes, double opacity)
    {
        opacity = Math.Clamp(opacity, 0.0, 1.0);

        using var source = SKBitmap.Decode(imageBytes);
        if (source == null) return imageBytes;

        // Built explicitly as straight (non-premultiplied) alpha — reusing the
        // decoded bitmap's own AlphaType (often Premul by default) and just
        // overwriting .Pixels with straight-alpha colors was verified, by testing,
        // to produce a corrupted dark result: colors written as if straight get
        // reinterpreted as premultiplied, darkening everything instead of fading it.
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var straightAlphaBitmap = new SKBitmap(info);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                var p = source.GetPixel(x, y);
                straightAlphaBitmap.SetPixel(x, y, new SKColor(p.Red, p.Green, p.Blue, (byte)(p.Alpha * opacity)));
            }
        }

        using var image = SKImage.FromBitmap(straightAlphaBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Applies AES-256 (PDF 2.0 / Standard V5 R6) encryption as a FINAL pass, using
    /// PDFsharp 6.2 rather than PdfSharpCore.
    ///
    /// Why a separate library for one step (RB-1): PdfSharpCore can only emit RC4-128,
    /// which enterprise security review rejects, and it also corrupts /Info strings when
    /// encrypting. Rather than migrate the whole post-processing pipeline — metadata,
    /// outline, watermark and merge are all already VERIFIED on PdfSharpCore — this runs
    /// PDFsharp over the finished bytes purely to encrypt them. PdfSharpCore therefore
    /// writes metadata/outline UNENCRYPTED, and PDFsharp encrypts afterwards, which also
    /// removes the cause of the /Info corruption.
    ///
    /// Known gap: PDFsharp exposes 7 permission flags, not PdfSharpCore's 8 —
    /// AllowAccessibilityExtract has no equivalent and is reported as unsupported.
    /// </summary>
    private static byte[] ApplyAes256Encryption(byte[] pdfBytes, RenderingOptions options, ILogger? logger, List<string>? diagnosticWarnings)
    {
        try
        {
            using var input = new MemoryStream(pdfBytes);
            var document = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

            document.SecurityHandler.SetEncryptionToV5(false); // V5 R6 = AES-256

            var security = document.SecuritySettings;
            if (!string.IsNullOrEmpty(options.OwnerPassword)) security.OwnerPassword = options.OwnerPassword;
            if (!string.IsNullOrEmpty(options.UserPassword)) security.UserPassword = options.UserPassword;

            security.PermitPrint = options.AllowPrinting;
            security.PermitExtractContent = options.AllowCopyContent;
            security.PermitAnnotations = options.AllowAnnotations;
            security.PermitModifyDocument = options.AllowModifyContents;
            security.PermitFormsFill = options.AllowFillingForms;
            security.PermitAssembleDocument = options.AllowAssembleDocument;
            security.PermitFullQualityPrint = options.AllowFullQualityPrinting;

            if (!options.AllowAccessibilityExtract)
            {
                const string msg = "Encryption notice: AllowAccessibilityExtract=false was requested but the AES-256 encryption backend does not expose that permission bit; content extraction for accessibility remains permitted.";
                logger?.LogWarning(msg);
                diagnosticWarnings?.Add(msg);
            }

            using var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            // Fail closed: an unencrypted PDF must never be returned when the caller
            // asked for encryption — that would silently break their security model.
            logger?.LogError(ex, "AES-256 encryption pass failed.");
            throw new InvalidOperationException("Failed to apply AES-256 encryption to the generated PDF.", ex);
        }
    }

    /// <summary>
    /// Adds the two structural elements PDF/A conformance actually requires beyond
    /// what Chromium's renderer already provides for free (full font embedding,
    /// verified separately): an /OutputIntents entry carrying an embedded sRGB ICC
    /// profile, and an XMP metadata stream declaring the conformance level. Verified
    /// end-to-end against real veraPDF validation (144/144 PDF/A-2b rules passed,
    /// 0 failures) on actual Chromium-rendered output — not just "should work"
    /// reasoning. PdfSharpCore has no first-class PDF/A API; this builds the
    /// required dictionary/stream objects directly via its low-level PdfDictionary
    /// primitives, which is the same mechanism PdfSharpCore's own higher-level
    /// features (metadata, outline, encryption) are built on internally.
    /// </summary>
    private static void ApplyPdfaCompliance(PdfSharpCore.Pdf.PdfDocument document, RenderingOptions options, bool xmpOnly = false)
    {
        var conformancePart = string.Equals(options.PdfaCompliance, "PDF/A-3b", StringComparison.OrdinalIgnoreCase) ? "3" : "2";

        var catalog = document.Internals.Catalog;

        if (!xmpOnly)
        {
        var iccBytes = LoadEmbeddedSrgbIccProfile();

        var iccStreamObj = new PdfSharpCore.Pdf.PdfDictionary(document);
        document.Internals.AddObject(iccStreamObj);
        iccStreamObj.CreateStream(iccBytes);
        iccStreamObj.Elements.SetInteger("/N", 3);

        var outputIntent = new PdfSharpCore.Pdf.PdfDictionary(document);
        document.Internals.AddObject(outputIntent);
        outputIntent.Elements.SetName("/Type", "/OutputIntent");
        outputIntent.Elements.SetName("/S", "/GTS_PDFA1");
        outputIntent.Elements.SetString("/OutputConditionIdentifier", "sRGB IEC61966-2.1");
        outputIntent.Elements.SetString("/Info", "sRGB IEC61966-2.1");
        outputIntent.Elements.SetString("/RegistryName", "http://www.color.org");
        outputIntent.Elements.SetReference("/DestOutputProfile", iccStreamObj);

        var outputIntentsArray = new PdfSharpCore.Pdf.PdfArray(document);
        outputIntentsArray.Elements.Add(outputIntent);
        catalog.Elements.SetValue("/OutputIntents", outputIntentsArray);
        }

        var title = EscapeXmpText(options.Title ?? document.Info.Title ?? string.Empty);
        var author = EscapeXmpText(options.Author ?? document.Info.Author ?? string.Empty);
        var xmp = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
            (xmpOnly ? "" :
                "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">\n" +
                $"<pdfaid:part>{conformancePart}</pdfaid:part>\n" +
                "<pdfaid:conformance>B</pdfaid:conformance>\n" +
                "</rdf:Description>\n") +
            // PDF/UA identification schema - required by PDF/UA-1 clause 5 test 1.
            (options.GenerateTaggedPdf ?
                "<rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n" +
                "<pdfuaid:part>1</pdfuaid:part>\n" +
                "</rdf:Description>\n" : "") +
            // When PDF/A and PDF/UA are combined, PDF/A clause 6.6.2.3.1 additionally
            // requires every non-predefined XMP property to be declared in an extension
            // schema. Emitting pdfuaid without this block made a previously-compliant
            // PDF/A-2b document fail with 2 errors - caught by re-validating after the
            // change rather than assuming the addition was harmless.
            ((options.GenerateTaggedPdf && !xmpOnly) ?
                "<rdf:Description rdf:about=\"\" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">\n" +
                "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">\n" +
                "<pdfaSchema:schema>PDF/UA Universal Accessibility Schema</pdfaSchema:schema>\n" +
                "<pdfaSchema:namespaceURI>http://www.aiim.org/pdfua/ns/id/</pdfaSchema:namespaceURI>\n" +
                "<pdfaSchema:prefix>pdfuaid</pdfaSchema:prefix>\n" +
                "<pdfaSchema:property><rdf:Seq><rdf:li rdf:parseType=\"Resource\">\n" +
                "<pdfaProperty:name>part</pdfaProperty:name>\n" +
                "<pdfaProperty:valueType>Integer</pdfaProperty:valueType>\n" +
                "<pdfaProperty:category>internal</pdfaProperty:category>\n" +
                "<pdfaProperty:description>Indicates which part of ISO 14289 is followed</pdfaProperty:description>\n" +
                "</rdf:li></rdf:Seq></pdfaSchema:property>\n" +
                "</rdf:li></rdf:Bag></pdfaExtension:schemas>\n" +
                "</rdf:Description>\n" : "") +
            (string.IsNullOrEmpty(title) ? "" :
                "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
                $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>\n" +
                "</rdf:Description>\n") +
            (string.IsNullOrEmpty(author) ? "" :
                "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
                $"<dc:creator><rdf:Seq><rdf:li>{author}</rdf:li></rdf:Seq></dc:creator>\n" +
                "</rdf:Description>\n") +
            "<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n" +
            "<pdf:Producer>PdfEngine</pdf:Producer>\n" +
            "</rdf:Description>\n" +
            "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">\n" +
            "<xmp:CreatorTool>PdfEngine</xmp:CreatorTool>\n" +
            "</rdf:Description>\n" +
            "</rdf:RDF>\n" +
            "</x:xmpmeta>\n" +
            "<?xpacket end=\"w\"?>";

        var metadataObj = new PdfSharpCore.Pdf.PdfDictionary(document);
        document.Internals.AddObject(metadataObj);
        metadataObj.Elements.SetName("/Type", "/Metadata");
        metadataObj.Elements.SetName("/Subtype", "/XML");
        metadataObj.CreateStream(Encoding.UTF8.GetBytes(xmp));
        catalog.Elements.SetReference("/Metadata", metadataObj);
    }

    private static string EscapeXmpText(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static byte[] LoadEmbeddedSrgbIccProfile()
    {
        var assembly = typeof(PlaywrightPdfService).Assembly;
        const string resourceName = "PdfEngine.Infrastructure.Resources.sRGB.icc";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded ICC profile resource '{resourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Maps each cross-reference target id to the physical PDF page its anchor text
    /// actually landed on, by reading the rendered document rather than inferring it
    /// from DOM geometry (which was verified twice to produce wrong numbers — forced
    /// breaks make DOM scroll coordinates non-linear against real page boundaries).
    /// Whitespace is normalised on both sides because PDF text extraction inserts
    /// line breaks at layout positions that don't exist in the source markup.
    /// </summary>
    /// <param name="preferLastOnFallback">
    /// Which occurrence the SHORT fingerprint fallback should take. A table of contents
    /// repeats every section title verbatim, so a cross-reference wants the LAST match —
    /// the real section rather than the line pointing at it. Everything anchored to a
    /// position in document order (a running header's `string-set`, a footnote call, a page
    /// float) wants the FIRST: measured, a running header read "Footnotes" on the last page
    /// of a document because a summary table there happened to contain that word.
    /// </param>
    private static Dictionary<string, int> ResolvePageReferencesFromPdf(
        byte[] pdfBytes, List<PageRefRequest> requests, ILogger? logger = null,
        bool preferLastOnFallback = true)
    {
        var resolved = new Dictionary<string, int>();
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var pageTexts = document.GetPages()
                .Select(p => NormalizeForMatch(string.Join(" ", p.GetWords().Select(w => w.Text))))
                .ToList();

            foreach (var request in requests)
            {
                var needle = NormalizeForMatch(request.Fingerprint);

                // The fingerprint is tried whole, then at progressively shorter prefixes.
                // Extracted reading order is not always the visual order: a two-column
                // layout comes out of the page interleaved line by line, so a long
                // fingerprint that spans into a column stops matching part-way through
                // even though it is unmistakably on that page. Measured — a section laid
                // out in two columns failed its match entirely and fell back to naming the
                // table of contents. Every prefix stays anchored at the element's own
                // text, so shortening loses reach, never precision, and the floor keeps it
                // specific enough not to collide.
                foreach (var length in new[] { needle.Length, 60, 45, 32 })
                {
                    if (length < 24 || length > needle.Length) continue;
                    var candidate = needle[..length];
                    var page = pageTexts.FindIndex(t => t.Contains(candidate, StringComparison.Ordinal));
                    if (page >= 0) { resolved[request.Id] = page + 1; break; }
                }

                // Fallback: the extended fingerprint can straddle a page boundary and
                // then match nothing. Retry on the anchor's own text, taking the LAST
                // occurrence — a contents/index page that repeats the title almost
                // always precedes the section it points at, so the final match is the
                // real content rather than the reference to it.
                if (!resolved.ContainsKey(request.Id))
                {
                    var shortNeedle = NormalizeForMatch(request.ShortFingerprint);
                    if (shortNeedle.Length >= 3)
                    {
                        for (int step = 0; step < pageTexts.Count; step++)
                        {
                            var i = preferLastOnFallback ? pageTexts.Count - 1 - step : step;
                            if (pageTexts[i].Contains(shortNeedle, StringComparison.Ordinal))
                            {
                                resolved[request.Id] = i + 1;
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read rendered PDF while resolving page references.");
        }

        return resolved;
    }

    /// <summary>
    /// Normalises text for fingerprint matching. NFKC is essential, not cosmetic: fonts
    /// render "fi"/"fl" as single ligature glyphs (U+FB01/U+FB02), so a heading containing
    /// e.g. "financials" extracts as "ﬁnancials" and a naive substring match silently
    /// fails — leaving that one entry as "?" while every other entry in the same table of
    /// contents resolves correctly. NFKC decomposes ligatures back to their component
    /// characters on BOTH sides of the comparison.
    ///
    /// Whitespace is COLLAPSED, not removed. Removing it was tried and measured worse: it
    /// let long fingerprints match across boundaries they should not, and running headers
    /// started naming the wrong section. The disagreement it was meant to paper over —
    /// a table's `textContent` running cells together while the PDF extracts them spaced —
    /// is fixed where it belongs, in how the fingerprint is built (see the planner's
    /// `readableText`), not by weakening every comparison in the engine.
    ///
    /// Case is folded because `text-transform` is applied when the page is PAINTED but not
    /// when its text is read out of the DOM. A heading badge styled
    /// `text-transform: uppercase` extracts from the PDF in capitals while the fingerprint
    /// taken from the document keeps the author's case, and the two never match. Measured:
    /// every running header in a document whose section badges were uppercased failed its
    /// primary match and fell back to the table of contents, so nine of ten pages named the
    /// wrong section.
    /// </summary>
    private static string NormalizeForMatch(string value) =>
        Regex.Replace((value ?? string.Empty).Normalize(NormalizationForm.FormKC), @"\s+", " ")
             .Trim().ToLowerInvariant();

    // --- T1-1: running headers/footers via @page margin boxes -------------------

    /// <summary>
    /// Draws each declared <c>@page</c> margin box on every page it applies to.
    ///
    /// This exists because Chromium's <c>headerTemplate</c> is ONE fixed template for the
    /// whole document — it cannot vary per page, so "the current chapter in the header",
    /// the single most-requested feature of this class, is impossible through it. Drawing
    /// into the page margin during post-processing is the same mechanism that already
    /// stamps watermarks, so it inherits a proven path.
    ///
    /// <c>string()</c> follows the CSS spec's default <c>first</c> semantics: the value is
    /// the first assignment made ON that page, or, when the page contains none, the value
    /// carried forward from the most recent earlier assignment. That is what produces the
    /// expected behaviour of a chapter title persisting across the chapter's pages.
    /// </summary>
    private static byte[] StampMarginBoxes(
        byte[] pdfBytes, PaginationPlan plan, RenderingOptions options,
        ILogger? logger = null, List<string>? diagnosticWarnings = null)
    {
        using var input = new MemoryStream(pdfBytes);
        using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
            input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

        var total = document.PageCount;
        if (total == 0) return pdfBytes;

        // Where the text column actually is. The caller's margin options are authoritative
        // when set, but a document that leaves margins to its own `@page` rule sends 0
        // through the API — and the header was then drawn at the 36pt fallback while the
        // body sat at the CSS margin, visibly out of line with the column it heads.
        var contentBox = ResolveContentBoxPt(pdfBytes, options);

        var unresolved = plan.StringSetAssignments.Count(a => a.Page <= 0);
        if (unresolved > 0)
        {
            // Reported rather than silently rendering a blank or wrong header — the same
            // rule that governs unresolved cross-references.
            diagnosticWarnings?.Add(
                $"Running header notice: {unresolved} string-set assignment(s) could not be located in the rendered PDF, so pages before the first resolved value show an empty running header.");
        }

        for (var index = 0; index < total; index++)
        {
            var pageNumber = index + 1;
            var page = document.Pages[index];

            foreach (var box in plan.MarginBoxes)
            {
                if (!AppliesToPage(box.PageSelector, pageNumber)) continue;

                var text = EvaluateMarginBoxContent(box.Content, plan, pageNumber, total);
                if (string.IsNullOrWhiteSpace(text)) continue;

                DrawMarginBoxText(page, box, text, contentBox);
            }
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// Page-selector match. <c>:left</c>/<c>:right</c> follow the CSS convention for
    /// left-to-right documents: page 1 is a right-hand page, so odd pages are right.
    /// </summary>
    private static bool AppliesToPage(string? selector, int pageNumber) => selector switch
    {
        null or "" => true,
        "first" => pageNumber == 1,
        "left" => pageNumber % 2 == 0,
        "right" => pageNumber % 2 == 1,
        _ => true
    };

    private static readonly Regex MarginBoxTokenPattern = new(
        @"string\s*\(\s*(?<name>[\w-]+)\s*(?:,\s*(?<which>first|last|start))?\s*\)"
        + @"|counter\s*\(\s*(?<counter>page|pages)\s*\)"
        + @"|""(?<literal>[^""]*)""|'(?<literal2>[^']*)'",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Evaluates a margin-box <c>content:</c> expression for one page.</summary>
    private static string EvaluateMarginBoxContent(
        string content, PaginationPlan plan, int pageNumber, int totalPages)
    {
        var sb = new StringBuilder();
        foreach (Match m in MarginBoxTokenPattern.Matches(content))
        {
            if (m.Groups["name"].Success)
            {
                sb.Append(ResolveStringValue(plan, m.Groups["name"].Value,
                    m.Groups["which"].Success ? m.Groups["which"].Value.ToLowerInvariant() : "first",
                    pageNumber));
            }
            else if (m.Groups["counter"].Success)
            {
                sb.Append(m.Groups["counter"].Value.Equals("pages", StringComparison.OrdinalIgnoreCase)
                    ? totalPages : pageNumber);
            }
            else if (m.Groups["literal"].Success) sb.Append(m.Groups["literal"].Value);
            else if (m.Groups["literal2"].Success) sb.Append(m.Groups["literal2"].Value);
        }
        return sb.ToString();
    }

    private static string ResolveStringValue(
        PaginationPlan plan, string name, string which, int pageNumber)
    {
        var candidates = plan.StringSetAssignments
            .Where(a => a.Page > 0 && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Page).ThenBy(a => a.DocumentOrder)
            .ToList();
        if (candidates.Count == 0) return string.Empty;

        var onThisPage = candidates.Where(a => a.Page == pageNumber).ToList();
        if (onThisPage.Count > 0)
        {
            // `last` takes the final assignment on the page; `first`/`start` take the
            // first. This is the only place the three keywords differ in practice.
            return (which == "last" ? onThisPage[^1] : onThisPage[0]).Value;
        }

        // No assignment on this page: carry the most recent earlier one forward. Without
        // this a chapter title would appear only on the chapter's opening page.
        var carried = candidates.LastOrDefault(a => a.Page < pageNumber);
        return carried?.Value ?? string.Empty;
    }

    /// <summary>
    /// The document's text column, as insets in points from each edge. Measured from the
    /// rendered PDF when the caller left a margin unset, so an engine-drawn header lines up
    /// with the body text whether the margins came from the API or from `@page` CSS.
    /// </summary>
    private static (double Left, double Right, double Top, double Bottom) ResolveContentBoxPt(
        byte[] pdfBytes, RenderingOptions options)
    {
        var left = PaginationPlanner.ParseCssSizeToPx(options.MarginLeft) * 0.75;
        var right = PaginationPlanner.ParseCssSizeToPx(options.MarginRight) * 0.75;
        var top = PaginationPlanner.ParseCssSizeToPx(options.MarginTop) * 0.75;
        var bottom = PaginationPlanner.ParseCssSizeToPx(options.MarginBottom) * 0.75;
        if (left > 1 && right > 1 && top > 1 && bottom > 1) return (left, right, top, bottom);

        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            double? mLeft = null, mRight = null, mTop = null, mBottom = null;
            foreach (var pdfPage in document.GetPages())
            {
                foreach (var word in pdfPage.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(word.Text)) continue;
                    var b = word.BoundingBox;
                    if (mLeft == null || b.Left < mLeft) mLeft = b.Left;
                    if (mRight == null || pdfPage.Width - b.Right < mRight) mRight = pdfPage.Width - b.Right;
                    if (mTop == null || pdfPage.Height - b.Top < mTop) mTop = pdfPage.Height - b.Top;
                    if (mBottom == null || b.Bottom < mBottom) mBottom = b.Bottom;
                }
            }
            // A floor of 18pt keeps a box off the very edge of the sheet, which most
            // printers crop.
            if (left <= 1 && mLeft != null) left = Math.Max(18, mLeft.Value);
            if (right <= 1 && mRight != null) right = Math.Max(18, mRight.Value);
            if (top <= 1 && mTop != null) top = Math.Max(18, mTop.Value);
            if (bottom <= 1 && mBottom != null) bottom = Math.Max(18, mBottom.Value);
        }
        catch (Exception)
        {
            // An unreadable (e.g. already-encrypted) document falls back to the same
            // default the stamper has always used.
        }

        return (left > 1 ? left : 36, right > 1 ? right : 36,
                top > 1 ? top : 36, bottom > 1 ? bottom : 36);
    }

    private static void DrawMarginBoxText(
        PdfSharpCore.Pdf.PdfPage page, MarginBoxRequest box, string text,
        (double Left, double Right, double Top, double Bottom) contentBox)
    {
        // A running header or folio is pagination furniture by definition: it repeats on
        // every page and belongs to none of them. Declaring it so is what lets a tagged
        // document keep its PDF/UA conformance while still having a header.
        var subtype = box.Box.StartsWith("top", StringComparison.OrdinalIgnoreCase)
            ? "/Header" : "/Footer";
        BeginArtifact(page, "/Pagination", subtype);
        using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(
            page, PdfSharpCore.Drawing.XGraphicsPdfPageOptions.Append);

        var font = new PdfSharpCore.Drawing.XFont(
            string.IsNullOrWhiteSpace(box.FontFamily) ? "Helvetica" : box.FontFamily,
            box.FontSize <= 0 ? 9 : box.FontSize);
        var brush = new PdfSharpCore.Drawing.XSolidBrush(ParseColor(box.Color));

        // Placed INSIDE the page margin, which is where a margin box belongs — drawing it
        // in the content area would overlap the document's own text.
        var isTop = box.Box.StartsWith("top", StringComparison.OrdinalIgnoreCase);
        var edge = isTop ? contentBox.Top : contentBox.Bottom;
        var y = isTop ? Math.Max(edge * 0.45, box.FontSize + 2)
                      : page.Height.Point - Math.Max(edge * 0.45, box.FontSize + 2);

        var align = box.Box.EndsWith("left", StringComparison.OrdinalIgnoreCase)
            ? PdfSharpCore.Drawing.XStringAlignment.Near
            : box.Box.EndsWith("right", StringComparison.OrdinalIgnoreCase)
                ? PdfSharpCore.Drawing.XStringAlignment.Far
                : PdfSharpCore.Drawing.XStringAlignment.Center;

        // Aligned with the text column, not with a guessed margin.
        var left = contentBox.Left;
        var width = Math.Max(72, page.Width.Point - contentBox.Left - contentBox.Right);
        var format = new PdfSharpCore.Drawing.XStringFormat { Alignment = align };

        gfx.DrawString(text, font, brush,
            new PdfSharpCore.Drawing.XRect(left, y - box.FontSize, width, box.FontSize * 1.4), format);
        // XGraphics writes into the stream it appended at construction, so the block has
        // to be closed after it is disposed, not before.
        gfx.Dispose();
        EndArtifact(page);
    }


    // --- T1-5: footnotes ---------------------------------------------------------

    /// <summary>
    /// How one render's reserved bands compare with the space available to them.
    ///
    /// Both edges are measured together because they compete for the same page: footnotes
    /// and `float: bottom` share the bottom band, `float: top` takes the top, and
    /// reserving either one re-paginates the document and can move the other. Two
    /// independent loops would each undo the other's convergence.
    /// </summary>
    private sealed class PagedBandFit
    {
        public Dictionary<int, double> TopBands { get; } = new();
        public Dictionary<int, double> BottomBands { get; } = new();
        public Dictionary<int, double> TopFree { get; } = new();
        public Dictionary<int, double> BottomFree { get; } = new();

        /// <summary>Pages whose bands are taller than the page's whole content area. No
        /// amount of reserving fixes those, so they are reported instead.</summary>
        public List<int> Impossible { get; } = new();

        /// <summary>Largest shortfall at each edge, in points, across every affected page.</summary>
        public double TopDeficit { get; set; }
        public double BottomDeficit { get; set; }

        public double Deficit => Math.Max(TopDeficit, BottomDeficit);

        public IEnumerable<int> ShortPages =>
            TopBands.Keys.Where(p => TopFree.GetValueOrDefault(p, 0) < TopBands[p] - 1.0)
                .Concat(BottomBands.Keys.Where(p => BottomFree.GetValueOrDefault(p, 0) < BottomBands[p] - 1.0))
                .Distinct().OrderBy(p => p);

        /// <summary>
        /// The tallest band any single page needs. Growing a margin by this much gives
        /// EVERY page at least that much free space, so it is the value a uniform
        /// reservation should jump straight to.
        /// </summary>
        public double MaxTopBand => TopBands.Count == 0 ? 0 : TopBands.Values.Max();
        public double MaxBottomBand => BottomBands.Count == 0 ? 0 : BottomBands.Values.Max();

        /// <summary>
        /// Pages still short of bottom-band space, lowest page number first — which is the
        /// order per-page reservation has to work in. A break on page N shifts every page
        /// after it, so only the lowest-numbered one can be trusted from any single render.
        /// </summary>
        public IEnumerable<(int Page, double Needed)> BottomDeficitsInOrder =>
            BottomBands.Keys
                .Where(p => BottomFree.GetValueOrDefault(p, 0) < BottomBands[p] - 1.0)
                .OrderBy(p => p)
                .Select(p => (p, BottomBands[p]));
    }

    /// <summary>
    /// Places everything that has been lifted out of the text flow — T1-5 footnotes and
    /// T1-8 page floats — into space reserved at the page edges.
    ///
    /// Chromium implements neither. Measured 2026-08-18: `float: footnote` content renders
    /// INLINE exactly where it was authored, and `float: top` and `float: bottom` produced
    /// identical output at the identical position, 38% down page 1. The planner has already
    /// lifted the content out; what is left — and what makes this the largest work in its
    /// tier — is that reserving space at a page edge CHANGES pagination, which can move an
    /// element to a different page, which changes which page needs reserving. So this is a
    /// bounded reflow loop, not a stamping pass.
    ///
    /// Both edges are driven by one loop on purpose. Footnotes and `float: bottom` share
    /// the bottom band and `float: top` takes the top, but all three change the same
    /// pagination — two independent loops would each undo the other's convergence.
    ///
    /// The reservation is made by growing the page box's margins, which means it is
    /// UNIFORM across the document rather than per-page. That is a deliberate trade, chosen
    /// after the per-page alternative was built and measured wrong. Per-page reservation
    /// means forcing a page break before whatever content would be overrun — but a break
    /// inserted on page N shifts every later page, so every other page's measurement, taken
    /// from the same render, is stale the moment the first break is applied. Verified
    /// directly: a three-footnote document came out with two nearly empty pages, each
    /// holding a single paragraph, because the second page's break had been computed
    /// against the layout the first page's break destroyed. Making it correct requires one
    /// full re-render per affected page — unusable on exactly the legal and academic
    /// documents these features exist for. Growing the margins instead hands the
    /// re-pagination back to Chromium, which does it correctly in one pass.
    ///
    /// The cost, stated plainly: pages carrying little or nothing at an edge lose the same
    /// band as the page carrying the most. Reclaiming that is tracked as T1-9.
    ///
    /// The loop stops as soon as every affected page has room, and REPORTS rather than
    /// silently shipping overlapping content if it runs out of passes.
    /// </summary>
    private async Task<byte[]> ApplyPagedBandReflowAsync(
        IPage page, byte[] pdfBytes, Func<Task<byte[]>> renderDocumentAsync, PaginationPlan plan,
        RenderingOptions options, PageOverrideState overrides, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var maxPasses = Math.Clamp(options.MaxFootnoteReflowPasses, 1, 8);


        // Both bases are fixed once, from the render made before anything was reserved:
        // every later render has grown margins, so measuring them again would chase itself.
        var bottomBaseY = ResolveBandBasePt(pdfBytes, options.MarginBottom, fromTop: false);
        var topBase = ResolveBandBasePt(pdfBytes, options.MarginTop, fromTop: true);
        plan.FootnoteBandBaseYPt = bottomBaseY;
        plan.FloatBandBaseTopPt = topBase;

        var reservedTopPt = 0.0;
        var reservedBottomPt = 0.0;
        var passesUsed = 0;
        PagedBandFit? fit = null;

        // T1-9. Per-page reservation costs one render per page that needs a band, so the
        // pass budget has to be bigger than the uniform loop's and is counted separately.
        var mode = (options.FootnoteReservationMode ?? "auto").Trim().ToLowerInvariant();
        var perPage = mode == "per-page";
        var decideAutomatically = mode is not ("per-page" or "uniform");
        var perPageBudget = Math.Clamp(options.MaxPerPageReservationPasses, 1, 40);
        var perPagePassesUsed = 0;
        var decisionReported = false;
        maxPasses = Math.Max(maxPasses, perPageBudget + 2);

        for (var pass = 1; pass <= maxPasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            passesUsed = pass;

            ResolveLiftedContentPages(pdfBytes, plan, _logger, warnings, reportUnresolved: pass == 1);
            fit = MeasurePagedBandFit(pdfBytes, plan, options, topBase, bottomBaseY);

            // The caller should not have to know which of their documents benefits from
            // per-page reservation — the engine has just measured the only thing that
            // decides it. Uniform sacrifices the tallest band on EVERY page; per-page
            // sacrifices each page's own band and nothing on the pages without one. The
            // difference between those two numbers is the space at stake, and the number
            // of pages carrying a band is the number of extra renders it would cost.
            if (decideAutomatically && !decisionReported && fit.BottomBands.Count > 0)
            {
                perPage = DecidePerPageReservation(
                    pdfBytes, fit, options, topBase, bottomBaseY, perPageBudget, warnings);
                decisionReported = true;
            }

            // Convergence is tested against MEASURED free space, not against the margins we
            // asked for. Chromium resolves the print margin against the document's own
            // `@page` rule, so a requested reservation does not always take effect at the
            // size requested — and a loop that trusted its own request would report success
            // while shipping overlapping content.
            if (fit.Deficit <= 1.0) break;
            if (pass == maxPasses) break;

            // T1-9: per-page mode places ONE page's band per pass, then re-renders. The
            // top edge stays uniform in both modes — content cannot be pushed upwards, so
            // there is no break that reserves the top of a page.
            if (perPage && fit.BottomDeficit > 1.0)
            {
                var placed = await TryReserveOneBottomBandAsync(
                    page, pdfBytes, fit, bottomBaseY, perPageBudget - perPagePassesUsed, warnings);
                perPagePassesUsed += placed.PassesUsed;

                if (placed.Applied)
                {
                    reservedTopPt = Math.Max(reservedTopPt, fit.MaxTopBand);
                    if (reservedTopPt > 0) overrides.TopMarginPt = topBase + reservedTopPt;
                    await ClearStalePlannerBreaksAsync(page, plan, overrides, warnings);
                    pdfBytes = await renderDocumentAsync();
                    continue;
                }

                // Nothing could be placed by breaking — either the budget is spent or the
                // content will not move. Falling back to the uniform reservation is what
                // keeps this from degrading into overlapping text.
                if (placed.FallBackToUniform)
                {
                    perPage = false;
                    warnings.Add(placed.Reason!);
                }
            }

            // Jump straight to the tallest band any page needs rather than creeping up by
            // the current shortfall. Growing a margin by R gives every page R of free
            // space, so R = max(band) is sufficient in ONE step; creeping converged so
            // slowly that a 3-page, 8-footnote document still overlapped after four passes.
            // Reservations only ever grow, which is what makes the loop terminate.
            reservedTopPt = Math.Max(reservedTopPt, fit.MaxTopBand);
            reservedBottomPt = Math.Max(reservedBottomPt, fit.MaxBottomBand);

            if (reservedTopPt > 0) overrides.TopMarginPt = topBase + reservedTopPt;
            if (reservedBottomPt > 0) overrides.BottomMarginPt = bottomBaseY + reservedBottomPt;

            await ClearStalePlannerBreaksAsync(page, plan, overrides, warnings);

            pdfBytes = await renderDocumentAsync();
        }

        if (fit is { Impossible.Count: > 0 })
        {
            warnings.Add(
                $"Layout warning: on page(s) {string.Join(", ", fit.Impossible.Take(5))} the footnotes and/or page floats are taller than the page's entire content area, so no amount of reflow can fit them and they overlap the body text. Split the content or shorten it.");
        }
        else if (fit is { Deficit: > 1.0 })
        {
            var shortPages = fit.ShortPages.Take(5).ToList();
            warnings.Add(
                $"Layout warning: footnote/page-float placement did not settle within {passesUsed} reflow pass(es); page(s) {string.Join(", ", shortPages)} have less edge space than their content needs, so it may overlap the body text. Raising 'maxFootnoteReflowPasses' or reducing how much is placed on a single page will usually resolve it.");
        }

        return pdfBytes;
    }

    /// <summary>
    /// Chooses between a uniform and a per-page reservation from what has just been
    /// measured, and says why.
    ///
    /// This exists so the choice is not pushed onto the caller. Whether per-page pays off
    /// is a property of the DOCUMENT — how unevenly its footnotes are distributed — which
    /// the caller cannot see from the HTML but the engine can see exactly, once, after the
    /// first render. Leaving it as a manual switch means either every document pays for
    /// extra renders it does not need, or documents that would gain a page of content
    /// silently do not.
    /// </summary>
    private static bool DecidePerPageReservation(
        byte[] pdfBytes, PagedBandFit fit, RenderingOptions options,
        double topBasePt, double bottomBaseYPt, int budget, List<string> warnings)
    {
        int totalPages;
        double contentHeightPt;
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            totalPages = document.NumberOfPages;
            var first = document.GetPage(1);
            contentHeightPt = Math.Max(72, first.Height - topBasePt - bottomBaseYPt);
        }
        catch (Exception)
        {
            return false;   // cannot measure the trade, so take the cheap, always-correct one
        }

        var bandedPages = fit.BottomBands.Count;
        var tallest = fit.MaxBottomBand;

        // What each strategy costs the document, in points of usable height.
        var uniformCostPt = tallest * totalPages;
        var perPageCostPt = fit.BottomBands.Values.Sum();
        var savingPt = uniformCostPt - perPageCostPt;

        // Worth an extra render per banded page only if it buys back real estate on the
        // scale of a page, not a few points. A quarter of a page is the threshold, and it
        // was calibrated rather than guessed: a document with five footnotes crowded onto
        // one page and five plain pages after it reclaims ~0.3 of a page, which is a
        // visible gain (one footnote-free page went from a 143pt bottom gap to 67pt) for
        // one extra render. A half-page threshold rejected exactly that case.
        var worthwhile = savingPt >= contentHeightPt * 0.25 && savingPt >= 40;
        var affordable = bandedPages <= budget;
        var choosePerPage = worthwhile && affordable;

        var saving = $"{savingPt:0}pt (~{savingPt / contentHeightPt:0.0} page(s) of height)";
        if (choosePerPage)
        {
            warnings.Add(
                $"Footnote reservation: PER-PAGE chosen automatically. Footnotes are unevenly spread across {totalPages} page(s), so reserving uniformly would cost {saving} more than reserving per page. This costs up to {bandedPages} extra render(s). Set 'footnoteReservationMode' to \"uniform\" to force the cheaper strategy.");
        }
        else if (!affordable && worthwhile)
        {
            warnings.Add(
                $"Footnote reservation: UNIFORM chosen automatically. Per-page would reclaim {saving}, but {bandedPages} page(s) carry footnotes and each costs one extra render — beyond the budget of {budget}. Raise 'maxPerPageReservationPasses' and set 'footnoteReservationMode' to \"per-page\" to take that trade.");
        }
        else
        {
            warnings.Add(
                $"Footnote reservation: UNIFORM chosen automatically. Footnotes are spread evenly enough that reserving per page would reclaim only {saving} — not worth up to {bandedPages} extra render(s).");
        }

        return choosePerPage;
    }

    /// <summary>Outcome of one attempt to clear the bottom of a single page.</summary>
    private readonly record struct BandPlacement(
        bool Applied, int PassesUsed, bool FallBackToUniform, string? Reason);

    /// <summary>
    /// Places the band for the LOWEST-numbered page still short of bottom space, by
    /// forcing a page break before the content that would be overrun.
    ///
    /// Only one page per render, and that is the whole cost of per-page reservation rather
    /// than an implementation shortcut: a break on page N moves every page after it, so
    /// every other page's measurement — taken from the same render — is stale the instant
    /// this one is applied. Measured directly: applying several at once produced pages
    /// holding a single paragraph each.
    /// </summary>
    private async Task<BandPlacement> TryReserveOneBottomBandAsync(
        IPage page, byte[] pdfBytes, PagedBandFit fit, double bottomBaseYPt,
        int remainingBudget, List<string> warnings)
    {
        var deficits = fit.BottomDeficitsInOrder.ToList();
        if (deficits.Count == 0) return new BandPlacement(false, 0, false, null);

        if (remainingBudget <= 0)
        {
            return new BandPlacement(false, 0, true,
                $"Footnote notice: per-page reservation ran out of passes with {deficits.Count} page(s) still to place, so the remainder falls back to a uniform reservation (correct, just less tight). Raise 'maxPerPageReservationPasses' to tighten more pages — each one costs an extra render.");
        }

        var target = deficits[0];

        using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
        if (target.Page < 1 || target.Page > document.NumberOfPages)
        {
            return new BandPlacement(false, 0, true,
                "Footnote notice: per-page reservation could not read the page it needed to place, so a uniform reservation was used instead.");
        }

        var anchor = FindBandBreakAnchor(document.GetPage(target.Page), bottomBaseYPt + target.Needed);
        if (anchor == null)
        {
            // The page's first word is already inside the band: the content on this page
            // is not divisible any further, so no break helps it.
            return new BandPlacement(false, 0, true,
                $"Footnote notice: page {target.Page} carries a band too tall to clear by moving content, so a uniform reservation was used instead.");
        }

        var result = await ApplyPerPageBreakAsync(page, anchor.Value.Needle, anchor.Value.ShortNeedle);
        if (result > 0) return new BandPlacement(true, 1, false, null);

        return new BandPlacement(false, 0, true, result switch
        {
            -2 => $"Footnote notice: page {target.Page} could not be tightened further (the content that would move is already at a page boundary), so a uniform reservation was used instead.",
            _ => $"Footnote notice: the content on page {target.Page} could not be located in the document by text, so a uniform reservation was used instead. Text repeated verbatim elsewhere in the document is the usual cause."
        });
    }

    /// <summary>
    /// Discards Pass 2's pre-render forced breaks once, the first time anything moves the
    /// page boundaries. They are estimates measured against the page height as it stood
    /// beforehand, and a stale one strands whatever follows it on a page of its own.
    /// </summary>
    private static async Task ClearStalePlannerBreaksAsync(
        IPage page, PaginationPlan plan, PageOverrideState overrides, List<string> warnings)
    {
        var clearBreaks = !overrides.PlannerBreaksCleared;
        var cleared = await ApplyPageOverridesAsync(page, plan, overrides, null, clearBreaks);
        if (!clearBreaks) return;

        overrides.PlannerBreaksCleared = true;
        if (cleared > 0)
        {
            warnings.Add(
                $"Pagination notice: reserving space at the page edges changed the height of every page, so {cleared} pre-render page break(s) the planner had estimated were discarded and pagination was left to the browser (which measures against the real page boundaries). Headings are kept with the text that follows them via 'break-after: avoid'.");
        }
    }

    /// <summary>
    /// Where a reserved band's outer edge sits, as a distance in points from the sheet's
    /// bottom edge (<paramref name="fromTop"/> false) or top edge (true).
    ///
    /// Normally that is the caller's own margin. When the caller set none and left the
    /// margin to the document's `@page` rule, it is MEASURED from the render rather than
    /// guessed, because a guess would put the band visibly too high or too low.
    ///
    /// It cannot put a band on top of the body text whatever it returns: free space is
    /// measured from this same value, so the reflow loop's guarantee is
    /// `bandHeight &lt;= measuredFreeSpace` — which is exactly the condition for the band,
    /// drawn from this base inwards, to clear the text. Getting this wrong costs vertical
    /// position, not correctness.
    /// </summary>
    private static double ResolveBandBasePt(byte[] pdfBytes, string? margin, bool fromTop)
    {
        var requestedPt = PaginationPlanner.ParseCssSizeToPx(margin) * 0.75;
        if (requestedPt > 1) return requestedPt;

        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            double? inset = null;
            foreach (var pdfPage in document.GetPages())
            {
                var words = pdfPage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                var top = HighestContentTopPt(pdfPage, words);
                var bottom = LowestContentBottomPt(pdfPage, words);
                if (top == null || bottom == null) continue;

                // Only a page whose content actually reaches down the sheet reveals where
                // the margin is. Measuring a page that holds two lines would report the
                // whole empty half of it as margin — which put a footnote band 42% down
                // the page, and then grew the reservation so far that a two-page document
                // came out as four. A short document is exactly the case with room to
                // spare, so falling back to the default costs nothing there.
                if (top.Value - bottom.Value < pdfPage.Height * 0.5) continue;

                var edge = fromTop ? pdfPage.Height - top.Value : bottom.Value;
                if (inset == null || edge < inset) inset = edge;
            }
            // A floor of 18pt keeps the band off the very edge of the sheet, which most
            // printers crop.
            if (inset != null) return Math.Max(18, inset.Value);
        }
        catch (Exception)
        {
            // Fall through to the same default the margin-box stamper uses.
        }

        return 36;
    }

    /// <summary>
    /// Assigns every lifted element — footnote calls and page floats alike — the page its
    /// ORIGINAL position actually landed on, read from the rendered PDF with the same
    /// fingerprint matcher as cross-references and running headers.
    /// </summary>
    private static void ResolveLiftedContentPages(
        byte[] pdfBytes, PaginationPlan plan, ILogger? logger,
        List<string>? warnings, bool reportUnresolved)
    {
        var requests = new List<PageRefRequest>();
        for (var i = 0; i < plan.Footnotes.Count; i++)
        {
            requests.Add(new PageRefRequest
            {
                Id = $"__footnote_{i}",
                Fingerprint = plan.Footnotes[i].Fingerprint,
                ShortFingerprint = plan.Footnotes[i].ShortFingerprint
            });
        }
        for (var i = 0; i < plan.PageFloats.Count; i++)
        {
            requests.Add(new PageRefRequest
            {
                Id = $"__pagefloat_{i}",
                Fingerprint = plan.PageFloats[i].Fingerprint,
                ShortFingerprint = plan.PageFloats[i].ShortFingerprint
            });
        }
        if (requests.Count == 0) return;

        var resolved = ResolvePageReferencesFromPdf(pdfBytes, requests, logger, preferLastOnFallback: false);
        var unresolvedFootnotes = 0;
        var unresolvedFloats = 0;

        // Falls back to the previous element's page rather than dropping anything. Content
        // on the wrong page is a visible, reportable error; content silently deleted from a
        // legal or financial document is not.
        var lastKnown = 0;
        for (var i = 0; i < plan.Footnotes.Count; i++)
        {
            if (resolved.TryGetValue($"__footnote_{i}", out var p) && p > 0)
            {
                plan.Footnotes[i].Page = p;
                lastKnown = p;
            }
            else
            {
                plan.Footnotes[i].Page = lastKnown > 0 ? lastKnown : 1;
                unresolvedFootnotes++;
            }
        }

        lastKnown = 0;
        for (var i = 0; i < plan.PageFloats.Count; i++)
        {
            if (resolved.TryGetValue($"__pagefloat_{i}", out var p) && p > 0)
            {
                plan.PageFloats[i].Page = p;
                lastKnown = p;
            }
            else
            {
                plan.PageFloats[i].Page = lastKnown > 0 ? lastKnown : 1;
                unresolvedFloats++;
            }
        }

        if (!reportUnresolved) return;

        if (unresolvedFootnotes > 0)
        {
            warnings?.Add(
                $"Footnote warning: {unresolvedFootnotes} footnote call(s) could not be located in the rendered PDF, so those footnotes were placed on the same page as the preceding footnote instead of their own. This usually means the text around the call is repeated elsewhere in the document.");
        }
        if (unresolvedFloats > 0)
        {
            warnings?.Add(
                $"Page float warning: {unresolvedFloats} floated element(s) could not be located in the rendered PDF, so they were placed on the same page as the preceding float instead of their own. A float surrounded by text that is repeated elsewhere in the document is the usual cause.");
        }
    }

    /// <summary>
    /// Measures, for the render just produced, how much space each page's reserved bands
    /// need at each edge versus how much that edge actually has.
    /// </summary>
    private static PagedBandFit MeasurePagedBandFit(
        byte[] pdfBytes, PaginationPlan plan, RenderingOptions options,
        double topBasePt, double bottomBaseYPt)
    {
        var result = new PagedBandFit();

        var footnotes = plan.Footnotes.Where(f => f.Page > 0).ToList();
        var floats = plan.PageFloats.Where(f => f.Page > 0 && f.ImageBase64.Length > 0).ToList();
        if (footnotes.Count == 0 && floats.Count == 0) return result;

        var marginLeftPt = ResolveMarginPt(options.MarginLeft);
        var marginRightPt = ResolveMarginPt(options.MarginRight);

        using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
        using var measure = PdfSharpCore.Drawing.XGraphics.CreateMeasureContext(
            new PdfSharpCore.Drawing.XSize(1000, 1000),
            PdfSharpCore.Drawing.XGraphicsUnit.Point,
            PdfSharpCore.Drawing.XPageDirection.Downwards);

        var pages = footnotes.Select(f => f.Page).Concat(floats.Select(f => f.Page))
            .Distinct().OrderBy(p => p);

        foreach (var pageNumber in pages)
        {
            if (pageNumber < 1 || pageNumber > document.NumberOfPages) continue;
            var pdfPage = document.GetPage(pageNumber);
            var contentWidth = Math.Max(72.0, pdfPage.Width - marginLeftPt - marginRightPt);

            // --- what this page needs ---
            var pageFootnotes = footnotes.Where(f => f.Page == pageNumber).OrderBy(f => f.Number).ToList();
            var bottomBand = pageFootnotes.Count > 0
                ? ComputeFootnoteBandHeightPt(measure, pageFootnotes, plan.FootnoteArea, contentWidth)
                : 0;

            var topBand = 0.0;
            foreach (var floated in floats.Where(f => f.Page == pageNumber))
            {
                var height = FitPageFloatHeightPt(floated, contentWidth) + PageFloatGapPt;
                if (floated.Edge == "top") topBand += height; else bottomBand += height;
            }

            // --- what this page has ---
            var words = pdfPage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            var contentHeight = pdfPage.Height - topBasePt - bottomBaseYPt;

            if (topBand + bottomBand > contentHeight)
            {
                // Reserving would leave no room for the page's own content. Looping on it
                // would never terminate, so it is recorded and reported instead.
                result.Impossible.Add(pageNumber);
                continue;
            }

            if (topBand > 0)
            {
                result.TopBands[pageNumber] = topBand;
                var highest = HighestContentTopPt(pdfPage, words);
                // Both in PDF coordinates (y up from the sheet's bottom edge): the content
                // area's top edge is pageHeight - topBase, and free space is whatever sits
                // between that edge and the topmost thing actually drawn.
                var free = Math.Max(0, (pdfPage.Height - topBasePt) - (highest ?? (pdfPage.Height - topBasePt)));
                result.TopFree[pageNumber] = free;
                result.TopDeficit = Math.Max(result.TopDeficit, topBand - free);
            }

            if (bottomBand > 0)
            {
                result.BottomBands[pageNumber] = bottomBand;
                var lowest = LowestContentBottomPt(pdfPage, words);
                var free = Math.Max(0, (lowest ?? (pdfPage.Height - bottomBaseYPt)) - bottomBaseYPt);
                result.BottomFree[pageNumber] = free;
                result.BottomDeficit = Math.Max(result.BottomDeficit, bottomBand - free);
            }
        }

        return result;
    }

    /// <summary>
    /// The highest point real content reaches on a page, in PDF coordinates. The mirror of
    /// <see cref="LowestContentBottomPt"/>, and it excludes page-filling artwork for the
    /// same reason: a full-bleed background touches the sheet edge on every page.
    /// </summary>
    private static double? HighestContentTopPt(
        UglyToad.PdfPig.Content.Page pdfPage, List<UglyToad.PdfPig.Content.Word> words)
    {
        double? highest = words.Count > 0 ? words.Max(w => w.BoundingBox.Top) : null;
        try
        {
            foreach (var image in pdfPage.GetImages())
            {
                var top = image.Bounds.Top;
                if (highest == null || top > highest) highest = top;
            }
        }
        catch (Exception)
        {
            // Same trade as the bottom edge: an unreadable image dictionary must not fail
            // the render, and the words alone still bound it usefully.
        }
        return highest;
    }

    /// <summary>
    /// The lowest point real content reaches on a page, in PDF coordinates (y up from the
    /// sheet's bottom edge). Words and images only: a full-bleed background rectangle
    /// reaches the sheet edge on every page and would report every page as completely full.
    /// </summary>
    private static double? LowestContentBottomPt(
        UglyToad.PdfPig.Content.Page pdfPage, List<UglyToad.PdfPig.Content.Word> words)
    {
        double? lowest = words.Count > 0 ? words.Min(w => w.BoundingBox.Bottom) : null;
        try
        {
            foreach (var image in pdfPage.GetImages())
            {
                var bottom = image.Bounds.Bottom;
                if (lowest == null || bottom < lowest) lowest = bottom;
            }
        }
        catch (Exception)
        {
            // An image whose dictionary PdfPig cannot read must not fail the whole render;
            // the words alone still give a usable — if slightly optimistic — bound.
        }
        return lowest;
    }

    /// <summary>
    /// Height of one page's footnote band, in points.
    ///
    /// Measured with the SAME font metrics and the SAME wrapping the drawing pass uses,
    /// deliberately: reserving space with browser metrics and then drawing with PDF
    /// metrics would leave the two free to disagree, and every point of disagreement is
    /// either wasted space or footnote text on top of body text.
    /// </summary>
    internal static double ComputeFootnoteBandHeightPt(
        PdfSharpCore.Drawing.XGraphics gfx, List<FootnoteAssignment> footnotes,
        FootnoteAreaRequest area, double contentWidthPt)
    {
        var height = area.SpaceAbovePt + area.SpaceBelowPt;
        if (area.SeparatorEnabled) height += Math.Max(0.25, area.SeparatorThicknessPt);

        foreach (var footnote in footnotes)
        {
            var font = ResolveFootnoteFont(area.FontFamily, footnote.FontSizePt);
            var indent = FootnoteIndentPt(gfx, footnote, font);
            var lines = LayoutFootnoteTokens(gfx, TokenizeFootnote(footnote),
                area.FontFamily, footnote.FontSizePt, Math.Max(24, contentWidthPt - indent));
            height += lines.Count * font.GetHeight() + area.ItemGapPt;
        }

        return height;
    }

    private static double FootnoteIndentPt(
        PdfSharpCore.Drawing.XGraphics gfx, FootnoteAssignment footnote, PdfSharpCore.Drawing.XFont font)
        => string.IsNullOrEmpty(footnote.Marker)
            ? 0
            : gfx.MeasureString(footnote.Marker + " ", font).Width;

    /// <summary>One word of footnote text, carrying the style it must be drawn in.</summary>
    internal readonly record struct FootnoteToken(
        string Text, bool Bold, bool Italic, string? Href, bool SpaceBefore);

    /// <summary>A token placed on a line, with its offset from the line's left edge.</summary>
    internal readonly record struct PlacedToken(FootnoteToken Token, double XPt, double WidthPt);

    /// <summary>
    /// Breaks a footnote into words that each remember their own style.
    ///
    /// A footnote that captured no runs — or one whose runs came back empty — falls back to
    /// the flattened text, so this never returns nothing to draw.
    /// </summary>
    internal static List<FootnoteToken> TokenizeFootnote(FootnoteAssignment footnote)
    {
        var tokens = new List<FootnoteToken>();

        // The runs are stitched back into one string first, keeping a note of which run
        // each character came from. Tokenising run by run instead loses the join: a
        // citation written "<b>Smith v. Jones</b>, 2026" puts the comma in its own run, and
        // splitting per run then re-spacing rendered it as "Jones , 2026". Whitespace in
        // the ORIGINAL text is the only thing that should produce a gap.
        var text = new StringBuilder();
        var styleOfChar = new List<FootnoteRun>();
        var fallback = new FootnoteRun();

        foreach (var run in footnote.Runs)
        {
            foreach (var ch in run.Text)
            {
                text.Append(ch);
                styleOfChar.Add(run);
            }
        }

        if (text.Length == 0)
        {
            foreach (var ch in footnote.Text ?? string.Empty)
            {
                text.Append(ch);
                styleOfChar.Add(fallback);
            }
        }

        var flat = text.ToString();
        var spaceBefore = false;
        var index = 0;

        while (index < flat.Length)
        {
            if (char.IsWhiteSpace(flat[index])) { spaceBefore = true; index++; continue; }

            var start = index;
            while (index < flat.Length && !char.IsWhiteSpace(flat[index])) index++;

            var style = styleOfChar[start];
            tokens.Add(new FootnoteToken(flat[start..index], style.Bold, style.Italic,
                style.Href, spaceBefore && tokens.Count > 0));
            spaceBefore = false;
        }

        return tokens;
    }

    /// <summary>
    /// Greedy word wrap that measures every word in ITS OWN font.
    ///
    /// Measuring the whole footnote in one font and then drawing parts of it bold would put
    /// the reserved height and the drawn height out of step, and every point of difference
    /// is either wasted space or footnote text on top of body text. Continuation lines are
    /// indented by the marker's width, which is what makes a footnote read as a footnote
    /// rather than as a paragraph.
    /// </summary>
    internal static List<List<PlacedToken>> LayoutFootnoteTokens(
        PdfSharpCore.Drawing.XGraphics gfx, List<FootnoteToken> tokens,
        string? family, double sizePt, double maxWidth)
    {
        var lines = new List<List<PlacedToken>>();
        var current = new List<PlacedToken>();
        var cursor = 0.0;
        var spaceWidth = gfx.MeasureString(" ", ResolveFootnoteFont(family, sizePt, false, false)).Width;

        foreach (var token in tokens)
        {
            var font = ResolveFootnoteFont(family, sizePt, token.Bold, token.Italic);
            var width = gfx.MeasureString(token.Text, font).Width;
            var advance = current.Count == 0 || !token.SpaceBefore ? 0 : spaceWidth;

            if (current.Count > 0 && cursor + advance + width > maxWidth)
            {
                lines.Add(current);
                current = new List<PlacedToken>();
                cursor = 0;
                advance = 0;
            }

            // A single word wider than the line — a long URL, or a script with no spaces.
            // Broken by binary search rather than one character at a time, because a
            // pathological token would otherwise be quadratic.
            if (current.Count == 0 && width > maxWidth)
            {
                var rest = token.Text;
                while (gfx.MeasureString(rest, font).Width > maxWidth && rest.Length > 1)
                {
                    int low = 1, high = rest.Length;
                    while (low < high)
                    {
                        var mid = (low + high + 1) / 2;
                        if (gfx.MeasureString(rest[..mid], font).Width <= maxWidth) low = mid;
                        else high = mid - 1;
                    }
                    var head = new FootnoteToken(rest[..low], token.Bold, token.Italic, token.Href, false);
                    lines.Add(new List<PlacedToken> { new(head, 0, gfx.MeasureString(head.Text, font).Width) });
                    rest = rest[low..];
                }
                width = gfx.MeasureString(rest, font).Width;
                current.Add(new PlacedToken(new FootnoteToken(rest, token.Bold, token.Italic, token.Href, false), 0, width));
                cursor = width;
                continue;
            }

            current.Add(new PlacedToken(token, cursor + advance, width));
            cursor += advance + width;
        }

        if (current.Count > 0) lines.Add(current);
        if (lines.Count == 0) lines.Add(new List<PlacedToken>());
        return lines;
    }

    /// <summary>
    /// The footnote band is drawn with PDF text operators, so it needs a font PdfSharpCore
    /// can actually resolve. A document typeface that is not installed falls back to the
    /// base-14 Helvetica rather than failing the render — the same trade already made for
    /// running headers.
    /// </summary>
    internal static PdfSharpCore.Drawing.XFont ResolveFootnoteFont(
        string? family, double sizePt, bool bold = false, bool italic = false)
    {
        var size = Math.Clamp(sizePt <= 0 ? 9 : sizePt, 5, 14);
        var style = (bold, italic) switch
        {
            (true, true) => PdfSharpCore.Drawing.XFontStyle.BoldItalic,
            (true, false) => PdfSharpCore.Drawing.XFontStyle.Bold,
            (false, true) => PdfSharpCore.Drawing.XFontStyle.Italic,
            _ => PdfSharpCore.Drawing.XFontStyle.Regular
        };

        foreach (var candidate in new[] { family, "Helvetica", "Arial" })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { return new PdfSharpCore.Drawing.XFont(candidate, size, style); }
            catch (Exception) { /* try the next candidate */ }
        }
        return new PdfSharpCore.Drawing.XFont("Helvetica", size);
    }

    /// <summary>
    /// Draws each page's footnotes into the space the reflow loop reserved for them.
    ///
    /// This runs after every re-render, because anything drawn into the PDF is discarded
    /// the moment the page is rendered again. It re-measures the finished document one
    /// last time and REPORTS any page whose band no longer fits — the page-reference
    /// substitution pass runs between the reflow loop and here, and it can nudge the
    /// layout.
    /// </summary>

    /// <summary>
    /// Adds a real <c>/Note</c> structure element for a footnote band, so a tagged document
    /// stays PDF/UA conformant WITHOUT lying about what the band is.
    ///
    /// The tempting fix was to declare the band an /Artifact like the running header. That
    /// would have taken veraPDF from 7 failed checks to 0 and made the document worse: an
    /// artifact is furniture a screen reader skips, and a footnote is the one piece of the
    /// page a reader most needs read to them. Passing the validator by hiding content from
    /// assistive technology is the opposite of accessibility.
    ///
    /// So the band is marked as CONTENT and joined to the structure tree properly, which
    /// needs four things kept consistent:
    ///   1. the drawing wrapped in <c>/Note &lt;&lt;/MCID n&gt;&gt; BDC … EMC</c>, with n
    ///      unused on that page;
    ///   2. a <c>/StructElem</c> of subtype /Note pointing at the page and that MCID;
    ///   3. that element parented into the document's own structure hierarchy;
    ///   4. the page's ParentTree entry extended so index n resolves back to the element.
    /// Miss the fourth and the tree LOOKS right in a dump while assistive technology cannot
    /// walk from the mark to its element — a broken tree is worse than an untagged one,
    /// because it reads as tagged.
    /// </summary>
    /// <returns>The MCID to mark the drawing with, or null when the document has no
    /// structure tree to join (an untagged render, where none of this applies).</returns>
    private static int? PrepareFootnoteNote(
        PdfSharpCore.Pdf.PdfDocument document, PdfSharpCore.Pdf.PdfPage page,
        int footnoteNumber, ILogger? logger)
    {
        try
        {
            var structRoot = document.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");
            if (structRoot == null) return null;

            // A page's marked content is indexed by MCID, so the new one has to be a number
            // Chromium did not already use on THIS page.
            var nextMcid = MaxMcidOnPage(page) + 1;

            var note = new PdfSharpCore.Pdf.PdfDictionary(document);
            document.Internals.AddObject(note);
            note.Elements["/Type"] = new PdfSharpCore.Pdf.PdfName("/StructElem");
            note.Elements["/S"] = new PdfSharpCore.Pdf.PdfName("/Note");
            note.Elements["/Pg"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(page);
            note.Elements["/K"] = new PdfSharpCore.Pdf.PdfInteger(nextMcid);
            // PDF/UA-1 requires a Note to be identifiable; the number is already unique
            // within the document because footnotes are numbered continuously.
            note.Elements["/ID"] = new PdfSharpCore.Pdf.PdfString($"footnote-{footnoteNumber}");
            note.Elements["/Alt"] = new PdfSharpCore.Pdf.PdfString($"Footnote {footnoteNumber}");

            // Parent it under whatever the document's top-level element is, rather than
            // under the paragraph that owns the call marker: the band is drawn at the foot
            // of the page and is not inside that paragraph's content.
            var parent = structRoot.Elements.GetDictionary("/K");
            if (parent == null) return null;
            note.Elements["/P"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(parent);
            var siblings = parent.Elements.GetArray("/K");
            if (siblings == null)
            {
                siblings = new PdfSharpCore.Pdf.PdfArray(document);
                parent.Elements["/K"] = siblings;
            }
            siblings.Elements.Add(PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(note));

            if (!AppendToParentTree(structRoot, page, note, nextMcid))
            {
                logger?.LogWarning(
                    "Footnote {Number}: could not extend the structure ParentTree, so the note is not left half-attached.",
                    footnoteNumber);
                siblings.Elements.RemoveAt(siblings.Elements.Count - 1);
                return null;
            }
            return nextMcid;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not tag footnote {Number} as a /Note; it will be drawn untagged.", footnoteNumber);
            return null;
        }
    }

    /// <summary>Highest /MCID already used in a page's content streams.</summary>
    private static int MaxMcidOnPage(PdfSharpCore.Pdf.PdfPage page)
    {
        var max = -1;
        try
        {
            foreach (var content in page.Contents)
            {
                var bytes = content.Stream?.UnfilteredValue;
                if (bytes == null || bytes.Length == 0) continue;
                var text = Encoding.ASCII.GetString(bytes);
                foreach (Match m in Regex.Matches(text, @"/MCID\s+(\d+)"))
                {
                    if (int.TryParse(m.Groups[1].Value, out var value) && value > max) max = value;
                }
            }
        }
        catch (Exception)
        {
            // An unreadable stream means we cannot prove a number is free, so start high
            // enough that a collision is implausible rather than guessing low.
            return Math.Max(max, 4095);
        }
        return max;
    }

    /// <summary>
    /// Makes the page's ParentTree entry resolve the new MCID back to its element. The
    /// entry is an array indexed BY MCID, so the element has to land at exactly that index
    /// — padding with nulls if Chromium left a gap.
    /// </summary>
    private static bool AppendToParentTree(
        PdfSharpCore.Pdf.PdfDictionary structRoot, PdfSharpCore.Pdf.PdfPage page,
        PdfSharpCore.Pdf.PdfDictionary note, int mcid)
    {
        var parentTree = structRoot.Elements.GetDictionary("/ParentTree");
        var nums = parentTree?.Elements.GetArray("/Nums");
        if (nums == null) return false;
        if (!page.Elements.ContainsKey("/StructParents")) return false;
        var structParents = page.Elements.GetInteger("/StructParents");

        // /Nums is a flat [key value key value …] list, so the value belonging to this
        // page's key is the element after it.
        for (var i = 0; i + 1 < nums.Elements.Count; i += 2)
        {
            if (nums.Elements[i] is not PdfSharpCore.Pdf.PdfInteger key || key.Value != structParents)
                continue;
            if (nums.Elements.GetArray(i + 1) is not { } entry) return false;
            while (entry.Elements.Count < mcid) entry.Elements.Add(PdfSharpCore.Pdf.PdfNull.Value);
            entry.Elements.Add(PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(note));
            return true;
        }
        return false;
    }

    private static byte[] StampFootnotes(
        byte[] pdfBytes, PaginationPlan plan, RenderingOptions options,
        ILogger? logger = null, List<string>? diagnosticWarnings = null)
    {
        var placed = plan.Footnotes.Where(f => f.Page > 0).ToList();
        if (placed.Count == 0) return pdfBytes;

        var area = plan.FootnoteArea;
        // The same base the reflow pass reserved against — recomputed here only if the
        // reflow pass never ran (it always does when there are footnotes, but a stamp
        // that silently drew at a different height than was reserved would be exactly
        // the class of bug the two passes exist to avoid).
        var bandBaseYPt = plan.FootnoteBandBaseYPt > 0
            ? plan.FootnoteBandBaseYPt
            : ResolveBandBasePt(pdfBytes, options.MarginBottom, fromTop: false);
        // Measured rather than assumed, for the same reason margin boxes are: a document
        // that leaves its margins to `@page` CSS sends 0 through the API, and the band
        // would otherwise be set at a fallback inset that does not line up with the text
        // column above it.
        var contentBox = ResolveContentBoxPt(pdfBytes, options);
        var marginLeftPt = contentBox.Left;
        var marginRightPt = contentBox.Right;

        // Free space is measured BEFORE anything is drawn, or the band would count itself
        // as content.
        var free = new Dictionary<int, double>();
        using (var probe = UglyToad.PdfPig.PdfDocument.Open(pdfBytes))
        {
            foreach (var pageNumber in placed.Select(f => f.Page).Distinct())
            {
                if (pageNumber < 1 || pageNumber > probe.NumberOfPages) continue;
                var probePage = probe.GetPage(pageNumber);
                var words = probePage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                var lowest = LowestContentBottomPt(probePage, words);
                free[pageNumber] = Math.Max(0, (lowest ?? (probePage.Height - bandBaseYPt)) - bandBaseYPt);
            }
        }

        using var input = new MemoryStream(pdfBytes);
        using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
            input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

        var overlapping = new List<int>();

        foreach (var group in placed.GroupBy(f => f.Page).OrderBy(g => g.Key))
        {
            if (group.Key < 1 || group.Key > document.PageCount) continue;

            var pdfPage = document.Pages[group.Key - 1];
            var contentWidth = Math.Max(72.0, pdfPage.Width.Point - marginLeftPt - marginRightPt);
            var items = group.OrderBy(f => f.Number).ToList();

            // A footnote band is CONTENT, so in a tagged document it joins the structure
            // tree as a /Note rather than being declared furniture. Prepared before the
            // drawing begins, because the marked-content block has to open first.
            int? noteMcid = options.GenerateTaggedPdf
                ? PrepareFootnoteNote(document, pdfPage, items[0].Number, logger)
                : null;
            if (noteMcid is { } mcid)
            {
                AppendRawContent(pdfPage, $"/Note << /MCID {mcid} >> BDC\n");
            }

            using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(
                pdfPage, PdfSharpCore.Drawing.XGraphicsPdfPageOptions.Append);

            var bandHeight = ComputeFootnoteBandHeightPt(gfx, items, area, contentWidth);
            if (free.TryGetValue(group.Key, out var available) && bandHeight > available + 1.0)
            {
                overlapping.Add(group.Key);
            }

            // Bottom-anchored: the band's floor is the caller's own bottom margin and it
            // grows upwards into the space the reflow pass reserved above it.
            var x = marginLeftPt;
            var y = pdfPage.Height.Point - bandBaseYPt - bandHeight + area.SpaceAbovePt;

            if (area.SeparatorEnabled)
            {
                var ruleWidth = area.SeparatorWidthPt > 0
                    ? Math.Min(area.SeparatorWidthPt, contentWidth)
                    : contentWidth * Math.Clamp(area.SeparatorWidthFraction, 0.02, 1.0);
                var thickness = Math.Max(0.25, area.SeparatorThicknessPt);
                gfx.DrawLine(new PdfSharpCore.Drawing.XPen(ParseColor(area.SeparatorColor), thickness),
                    x, y, x + ruleWidth, y);
                y += thickness;
            }

            y += area.SpaceBelowPt;

            var brush = new PdfSharpCore.Drawing.XSolidBrush(ParseColor(area.Color));
            var linkBrush = new PdfSharpCore.Drawing.XSolidBrush(ParseColor("#1d4ed8"));

            foreach (var footnote in items)
            {
                var baseFont = ResolveFootnoteFont(area.FontFamily, footnote.FontSizePt);
                var indent = FootnoteIndentPt(gfx, footnote, baseFont);
                var lineHeight = baseFont.GetHeight();
                var lines = LayoutFootnoteTokens(gfx, TokenizeFootnote(footnote),
                    area.FontFamily, footnote.FontSizePt, Math.Max(24, contentWidth - indent));

                for (var i = 0; i < lines.Count; i++)
                {
                    if (i == 0 && indent > 0)
                    {
                        gfx.DrawString(footnote.Marker + " ", baseFont, brush,
                            new PdfSharpCore.Drawing.XRect(x, y, indent, lineHeight),
                            PdfSharpCore.Drawing.XStringFormats.TopLeft);
                    }

                    foreach (var word in lines[i])
                    {
                        if (word.Token.Text.Length == 0) continue;
                        var font = ResolveFootnoteFont(area.FontFamily, footnote.FontSizePt,
                            word.Token.Bold, word.Token.Italic);
                        var isLink = !string.IsNullOrWhiteSpace(word.Token.Href);
                        var left = x + indent + word.XPt;

                        var rect = new PdfSharpCore.Drawing.XRect(left, y, word.WidthPt + 2, lineHeight);
                        gfx.DrawString(word.Token.Text, font, isLink ? linkBrush : brush, rect,
                            PdfSharpCore.Drawing.XStringFormats.TopLeft);

                        // Bold is deliberately NOT faked by double-striking the word. It
                        // was built and measured: drawing the glyphs twice does thicken the
                        // stem, but both copies land in the text layer, so the footnote
                        // extracted as "Smith Smith v.v. Jones" — copy, search and screen
                        // readers all corrupted to buy a visual approximation. Emphasis is
                        // reported as unrendered instead, which costs styling and nothing
                        // else. The style still reaches the font, so this starts working on
                        // its own the day bold and italic faces are available to resolve.

                        if (isLink)
                        {
                            // A real annotation, not just blue text: a citation link in a
                            // footnote is there to be followed.
                            AddFootnoteLink(pdfPage, word.Token.Href!, left, y, word.WidthPt, lineHeight);
                        }
                    }
                    y += lineHeight;
                }

                y += area.ItemGapPt;
            }

            if (noteMcid is not null)
            {
                // After the XGraphics is disposed: it writes into the stream it appended
                // at construction, so closing earlier would leave the band outside its own
                // marked-content block.
                gfx.Dispose();
                AppendRawContent(pdfPage, "EMC\n");
            }
        }

        if (overlapping.Count > 0)
        {
            diagnosticWarnings?.Add(
                $"Footnote warning: on page(s) {string.Join(", ", overlapping.Take(5))} the footnote block is taller than the space left free above the bottom margin, so it overlaps the body text. This is reported rather than hidden — the footnotes are still present and complete.");
            logger?.LogWarning("Footnote band overlaps body text on {Count} page(s).", overlapping.Count);
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }









    // --- T2-6: print production (bleed, crop marks, page boxes) --------------------

    /// <summary>Millimetres to points.</summary>
    private static double MmToPt(double mm) => mm * 72.0 / 25.4;

    /// <summary>How much sheet the crop marks need outside the bleed.</summary>
    private const double CropMarkAreaMm = 8;
    private const double CropMarkLengthMm = 4;

    /// <summary>
    /// Turns a page rendered at trim-plus-bleed into a print-ready sheet: the finished size
    /// recorded as the TrimBox, the bleed as the BleedBox, and optional crop marks on a
    /// slightly larger sheet.
    ///
    /// The boxes are the part that matters to a printer's workflow — they are how it knows
    /// where to cut. Drawing marks without setting them would look right and impose nothing.
    /// </summary>
    private static byte[] ApplyPrintProduction(
        byte[] pdfBytes, RenderingOptions options, double trimWidthPt, double trimHeightPt,
        ILogger? logger, List<string>? diagnosticWarnings)
    {
        var bleedPt = MmToPt(Math.Max(0, options.BleedMm));
        var markAreaPt = options.CropMarks ? MmToPt(CropMarkAreaMm) : 0;
        if (bleedPt <= 0 && markAreaPt <= 0) return pdfBytes;

        var source = Path.Combine(Path.GetTempPath(), $"pdfengine-print-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(source, pdfBytes);

            using var probeInput = new MemoryStream(pdfBytes);
            using var probe = PdfSharpCore.Pdf.IO.PdfReader.Open(
                probeInput, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);

            using var target = new PdfSharpCore.Pdf.PdfDocument();

            for (var index = 0; index < probe.PageCount; index++)
            {
                // What was rendered is trim + bleed on every side.
                var renderedWidth = probe.Pages[index].Width.Point;
                var renderedHeight = probe.Pages[index].Height.Point;

                var sheet = target.AddPage();
                sheet.Width = PdfSharpCore.Drawing.XUnit.FromPoint(renderedWidth + markAreaPt * 2);
                sheet.Height = PdfSharpCore.Drawing.XUnit.FromPoint(renderedHeight + markAreaPt * 2);

                // The finished page size, from the NOMINAL paper rather than the rendered
                // box, and where it sits on the sheet.
                var trimW = trimWidthPt > 0 ? trimWidthPt : renderedWidth - bleedPt * 2;
                var trimH = trimHeightPt > 0 ? trimHeightPt : renderedHeight - bleedPt * 2;
                var trimOffsetX = markAreaPt + (renderedWidth - trimW) / 2;
                var trimOffsetY = markAreaPt + (renderedHeight - trimH) / 2;

                using (var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(sheet))
                {
                    using var form = PdfSharpCore.Drawing.XPdfForm.FromFile(source);
                    form.PageIndex = index;
                    gfx.DrawImage(form, markAreaPt, markAreaPt, renderedWidth, renderedHeight);

                    if (options.CropMarks)
                    {
                        DrawCropMarks(gfx, sheet.Width.Point, sheet.Height.Point,
                            trimOffsetX, trimOffsetY, trimW, trimH, bleedPt);
                    }
                }

                // PDF box coordinates have their origin at the bottom-left of the sheet.
                var bleedLeft = markAreaPt;
                var bleedBottom = markAreaPt;
                SetPageBox(sheet, "/BleedBox", bleedLeft, bleedBottom,
                    bleedLeft + renderedWidth, bleedBottom + renderedHeight);

                // The finished page, taken from the NOMINAL size and centred — not from the
                // rendered box minus the bleed. Chromium rounds a requested pixel size to
                // whole device pixels, and letting that rounding through left the TrimBox
                // up to 0.3mm off the paper the job was ordered on. For print the TrimBox
                // IS the deliverable: it is the line the blade follows.
                var trimLeft = bleedLeft + (renderedWidth - trimW) / 2;
                var trimBottom = bleedBottom + (renderedHeight - trimH) / 2;
                SetPageBox(sheet, "/TrimBox", trimLeft, trimBottom, trimLeft + trimW, trimBottom + trimH);

                // ArtBox defaults to the trim: without it some workflows fall back to the
                // MediaBox and place the artwork including the crop-mark margin.
                SetPageBox(sheet, "/ArtBox", trimLeft, trimBottom, trimLeft + trimW, trimBottom + trimH);
            }

            using var output = new MemoryStream();
            target.Save(output);

            // Kept short on purpose: the diagnostics header truncates long warnings, and a
            // colour limitation that gets cut off mid-sentence is the one a printer finds
            // for you. The three facts that matter lead.
            diagnosticWarnings?.Add(
                $"Print notice: RGB colour, no CMYK conversion, PDF/X not claimed. Bleed {options.BleedMm:0.##}mm with a TrimBox{(options.CropMarks ? " and crop marks" : "")}. Confirm your printer accepts RGB.");

            return output.ToArray();
        }
        finally
        {
            try { if (File.Exists(source)) File.Delete(source); }
            catch (Exception) { /* temp cleanup is best effort */ }
        }
    }

    private static void SetPageBox(
        PdfSharpCore.Pdf.PdfPage page, string name, double left, double bottom, double right, double top)
    {
        var box = new PdfSharpCore.Pdf.PdfArray(page.Owner);
        foreach (var value in new[] { left, bottom, right, top })
        {
            box.Elements.Add(new PdfSharpCore.Pdf.PdfReal(Math.Round(value, 3)));
        }
        page.Elements[name] = box;
    }

    /// <summary>
    /// Draws the corner rules a printer cuts to. They start outside the bleed — a mark
    /// crossing into it would be trimmed into the artwork — and run outwards to the sheet
    /// edge.
    /// </summary>
    private static void DrawCropMarks(
        PdfSharpCore.Drawing.XGraphics gfx, double sheetWidth, double sheetHeight,
        double trimLeftPt, double trimTopPt, double trimWidthPt, double trimHeightPt, double bleedPt)
    {
        var pen = new PdfSharpCore.Drawing.XPen(PdfSharpCore.Drawing.XColors.Black, 0.25);
        var length = MmToPt(CropMarkLengthMm);

        // Keyed to the TRIM rectangle, which is what the blade follows.
        var left = trimLeftPt;
        var right = trimLeftPt + trimWidthPt;
        var top = trimTopPt;
        var bottom = trimTopPt + trimHeightPt;

        // Offset outwards past the bleed so no mark is printed inside the finished page.
        var gap = Math.Max(2, bleedPt);
        foreach (var (x, y, dx, dy) in new[]
        {
            (left, top, -1.0, -1.0), (right, top, 1.0, -1.0),
            (left, bottom, -1.0, 1.0), (right, bottom, 1.0, 1.0)
        })
        {
            gfx.DrawLine(pen, x + dx * gap, y, x + dx * (gap + length), y);
            gfx.DrawLine(pen, x, y + dy * gap, x, y + dy * (gap + length));
        }
    }

    // --- T2-5: linearization (fast web view) --------------------------------------

    /// <summary>
    /// Reorders the finished document for fast web view.
    ///
    /// Linearization moves the first page's objects to the front and adds a hint table, so
    /// a reader fetching the file over HTTP with range requests can paint page 1 before the
    /// rest arrives. It is a whole-file structural rewrite, which is why it runs after
    /// every pass that changes bytes and before the signature that seals them.
    ///
    /// Delegated to `qpdf` deliberately rather than reimplemented: linearization means
    /// rebuilding the cross-reference table, renumbering objects and emitting a correct
    /// hint stream, and a subtly wrong implementation produces a file that readers accept
    /// and silently fail to stream. qpdf is the reference implementation, is Apache-2.0,
    /// and is verified here by asking qpdf itself whether the result is linearized.
    /// </summary>
    private static byte[] ApplyLinearization(
        byte[] pdfBytes, RenderingOptions options, ILogger? logger, List<string>? diagnosticWarnings)
    {
        var input = Path.Combine(Path.GetTempPath(), $"pdfengine-lin-in-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"pdfengine-lin-out-{Guid.NewGuid():N}.pdf");

        try
        {
            File.WriteAllBytes(input, pdfBytes);

            var start = new System.Diagnostics.ProcessStartInfo("qpdf")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--linearize");

            // An encrypted document has to be opened to be rewritten, and qpdf preserves
            // the encryption on the way out (verified: an AES-256 file came back linearized
            // and still R=6). The password goes over STDIN, never in the argument list,
            // where it would be visible to anything that can read the process table.
            var password = options.UserPassword ?? options.OwnerPassword;
            var usePassword = !string.IsNullOrEmpty(password);
            if (usePassword) start.ArgumentList.Add("--password-file=-");

            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);

            using var process = System.Diagnostics.Process.Start(start);
            if (process == null)
            {
                throw new InvalidOperationException(
                    "Linearization requires the 'qpdf' binary, which could not be started. Install qpdf or set linearize=false.");
            }

            if (usePassword)
            {
                process.StandardInput.Write(password);
            }
            process.StandardInput.Close();

            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                throw new InvalidOperationException("Linearization timed out after 60 seconds.");
            }

            // qpdf exits 3 for warnings it recovered from; the output is still written and
            // usable, so only a hard failure is treated as one.
            if (process.ExitCode != 0 && process.ExitCode != 3)
            {
                throw new InvalidOperationException(
                    $"qpdf could not linearize the document (exit {process.ExitCode}): {stderr.Trim()}");
            }

            if (!File.Exists(output))
            {
                throw new InvalidOperationException("qpdf reported success but produced no output.");
            }

            var result = File.ReadAllBytes(output);
            if (process.ExitCode == 3)
            {
                logger?.LogWarning("qpdf linearized the document with warnings: {Warnings}", stderr.Trim());
            }
            return result;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The binary is not on PATH. Reported as a failure, not degraded: a caller who
            // asked for fast web view and silently did not get it has no way to notice.
            throw new InvalidOperationException(
                "Linearization requires the 'qpdf' binary and it was not found on PATH. Install qpdf, or set linearize=false.");
        }
        finally
        {
            foreach (var path in new[] { input, output })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception) { /* temp cleanup is best effort */ }
            }
        }
    }

    // --- T2-2: digital signatures -------------------------------------------------

    /// <summary>
    /// Reserves the signature slot without computing anything.
    ///
    /// PDFsharp builds the signature STRUCTURE correctly — the `/Sig` dictionary, the form
    /// field, the `/ByteRange` and a fixed-width `/Contents` placeholder — but its own
    /// signature computation cannot be used: measured on 6.2.1, the bytes it hands the
    /// signer (2,133, beginning with whitespace) are not the bytes its `/ByteRange`
    /// declares (2,922, beginning at the PDF header), so the result is a signature over
    /// content the file does not describe and openssl rejects it. The real signature is
    /// therefore computed afterwards, over exactly what the finished file declares, and
    /// patched into this placeholder.
    /// </summary>
    private sealed class SignaturePlaceholder : PdfSharp.Pdf.Signatures.IDigitalSigner
    {
        private readonly int _size;
        public SignaturePlaceholder(int size) => _size = size;

        public string CertificateName => "PDFEngine";
        public Task<int> GetSignatureSizeAsync() => Task.FromResult(_size);

        public Task<byte[]> GetSignatureAsync(Stream stream)
        {
            // Drained without ever asking whether it can seek: PDFsharp passes a
            // RangedStream whose CanSeek THROWS rather than returning false, and
            // Stream.CopyTo queries it.
            var buffer = new byte[81920];
            while (stream.Read(buffer, 0, buffer.Length) > 0) { }
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    /// <summary>Bytes reserved for the CMS blob. 4 KB comfortably holds an RSA-2048
    /// signature with its certificate chain; the slot is padded, never truncated.</summary>
    private const int SignatureSlotBytes = 4096;

    private static readonly Regex ByteRangePattern = new(
        @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]", RegexOptions.Compiled);

    /// <summary>
    /// Seals the finished document with a detached CMS signature (PAdES-style).
    ///
    /// Runs LAST, after every other pass, because a signature covers the file's bytes and
    /// anything written afterwards invalidates it.
    ///
    /// The signature is computed over exactly the two ranges the finished file declares in
    /// its `/ByteRange`, then written into the fixed-width `/Contents` placeholder — which
    /// cannot shift anything, because the slot's length does not change. That is what makes
    /// the result verifiable: the bytes signed and the bytes declared are the same bytes by
    /// construction.
    /// </summary>
    private static byte[] ApplyDigitalSignature(
        byte[] pdfBytes, RenderingOptions options, ILogger? logger, List<string>? diagnosticWarnings)
    {
        byte[] pkcs12;
        try
        {
            pkcs12 = Convert.FromBase64String(options.SigningCertificateBase64!);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "The signing certificate is not valid base64. Supply a PKCS#12 (.pfx/.p12) bundle, base64-encoded.");
        }

        using var certificate = LoadSigningCertificate(pkcs12, options.SigningCertificatePassword ?? string.Empty);

        // Rebuild the document through PDFsharp so it can lay out the signature structure.
        using var input = new MemoryStream(pdfBytes);
        var document = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

        var signatureOptions = new PdfSharp.Pdf.Signatures.DigitalSignatureOptions
        {
            Reason = options.SignatureReason ?? string.Empty,
            Location = options.SignatureLocation ?? string.Empty,
            PageIndex = 0
            // No Rectangle: an invisible signature. Drawing an appearance needs a font
            // resolver registered for PDFsharp too, which is a different one from the
            // resolver the engine registers for PdfSharpCore.
        };
        // A timestamp token carries its own certificate chain, so the slot has to be
        // bigger when one is requested. It is padded, never truncated — but a slot that is
        // too small fails the whole signature, so the size is chosen up front.
        var slotBytes = string.IsNullOrWhiteSpace(options.TimestampUrl)
            ? SignatureSlotBytes
            : SignatureSlotBytes * 4;
        PdfSharp.Pdf.Signatures.DigitalSignatureHandler.ForDocument(
            document, new SignaturePlaceholder(slotBytes), signatureOptions);

        using var staged = new MemoryStream();
        document.Save(staged, false);
        var bytes = staged.ToArray();

        var match = ByteRangePattern.Match(Encoding.Latin1.GetString(bytes));
        if (!match.Success)
        {
            throw new InvalidOperationException("The signature placeholder could not be located in the saved document.");
        }

        var firstStart = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var firstLength = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var secondStart = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var secondLength = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

        var signed = new byte[firstLength + secondLength];
        Buffer.BlockCopy(bytes, firstStart, signed, 0, firstLength);
        Buffer.BlockCopy(bytes, secondStart, signed, firstLength, secondLength);

        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        var signer = new CmsSigner(certificate)
        {
            // SHA-256. SHA-1 is refused by every modern validator.
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            // The whole chain travels with the signature, so a verifier does not have to
            // already hold the issuer to build a path.
            IncludeOption = X509IncludeOption.WholeChain
        };

        // Signing time is added explicitly, and adding ANY signed attribute makes .NET emit
        // the mandatory contentType and messageDigest attributes alongside it. Without them
        // the result is a bare PKCS#7 signature over the content — which openssl verifies
        // quite happily, and which is NOT what CAdES and PAdES require. Measured before
        // this: the CMS came out with `signedAttrs: <ABSENT>`.
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));

        cms.ComputeSignature(signer, silent: true);

        // PAdES B-T: attach a trusted timestamp. Without one the only evidence of when the
        // document was signed is the signer's own clock, and the signature becomes
        // unverifiable once the certificate expires.
        if (!string.IsNullOrWhiteSpace(options.TimestampUrl))
        {
            AttachTimestamp(cms, options.TimestampUrl!, logger);
        }

        var der = cms.Encode();

        // The placeholder sits between the two ranges: '<' at firstStart+firstLength and
        // '>' at secondStart-1.
        var slotStart = firstStart + firstLength + 1;
        var slotLength = secondStart - 1 - slotStart;
        if (der.Length * 2 > slotLength)
        {
            throw new InvalidOperationException(
                $"The signature is {der.Length} bytes and does not fit the {slotLength / 2}-byte slot reserved for it. A certificate with a long chain needs a larger reservation.");
        }

        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(der).PadRight(slotLength, '0'));
        Buffer.BlockCopy(hex, 0, bytes, slotStart, slotLength);

        // B-LT: append the evidence needed to validate this signature without reaching the
        // issuing authority. Everything the CMS carries is collected — the signer's chain
        // and, when the signature was timestamped, the authority's certificates too, since
        // the timestamp needs validating just as much as the signature does.
        if (options.EmbedValidationData)
        {
            var chain = new List<X509Certificate2> { certificate };
            chain.AddRange(cms.Certificates.OfType<X509Certificate2>());
            foreach (var signerInfo in cms.SignerInfos)
            {
                foreach (var attribute in signerInfo.UnsignedAttributes)
                {
                    if (attribute.Oid?.Value != TimestampAttributeOid) continue;
                    foreach (var value in attribute.Values)
                    {
                        try
                        {
                            var token = new SignedCms();
                            token.Decode(value.RawData);
                            chain.AddRange(token.Certificates.OfType<X509Certificate2>());
                        }
                        catch (Exception)
                        {
                            // A token this engine cannot re-read still signed the document
                            // correctly; only its certificates are missed from the store.
                        }
                    }
                }
            }

            bytes = ApplyDocumentSecurityStore(bytes, chain, logger, diagnosticWarnings);
        }

        logger?.LogInformation("Document signed with certificate {Subject} ({Bytes}-byte CMS).",
            certificate.Subject, der.Length);
        var level = string.IsNullOrWhiteSpace(options.TimestampUrl)
            ? "a basic signature (no trusted timestamp, so it stops being verifiable when the certificate expires — set 'timestampUrl' for PAdES B-T)"
            : "PAdES B-T, carrying a trusted timestamp";
        diagnosticWarnings?.Add(
            $"Signature notice: sealed by '{certificate.Subject}' as {level}. The signature is invisible — it appears in the reader's signature panel and draws nothing on the page. Any modification after this point breaks it.");

        return bytes;
    }

    /// <summary>OID of the CMS signature-timestamp unsigned attribute (RFC 3161).</summary>
    private const string TimestampAttributeOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>
    /// Requests an RFC 3161 timestamp over the signature and attaches it.
    ///
    /// The timestamp is taken over the SIGNATURE value, not the document — that is what
    /// binds "this signature existed at this time" and is what raises a basic signature to
    /// PAdES B-T. Everything needed is in the framework, so this costs no dependency: only
    /// a network call to the authority the caller chose.
    ///
    /// A failure here FAILS the signature rather than quietly producing an untimestamped
    /// one. A caller who asked for a timestamp did so because they need the signature to
    /// outlive the certificate, and silently handing back one that does not is the exact
    /// failure they would never notice.
    /// </summary>
    private static void AttachTimestamp(SignedCms cms, string timestampUrl, ILogger? logger)
    {
        if (!Uri.TryCreate(timestampUrl, UriKind.Absolute, out var authority)
            || (authority.Scheme != Uri.UriSchemeHttp && authority.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"'{timestampUrl}' is not a usable timestamp authority URL. Supply an http(s) RFC 3161 endpoint.");
        }

        var signerInfo = cms.SignerInfos[0];
        var request = Rfc3161TimestampRequest.CreateFromSignerInfo(
            signerInfo, HashAlgorithmName.SHA256, requestSignerCertificates: true);

        byte[] responseBytes;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var content = new ByteArrayContent(request.Encode());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");

            var response = http.PostAsync(authority, content).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The timestamp authority at {authority} returned HTTP {(int)response.StatusCode}.");
            }
            responseBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The timestamp authority at {authority} could not be reached: {ex.Message}", ex);
        }

        Rfc3161TimestampToken token;
        try
        {
            token = request.ProcessResponse(responseBytes, out _);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The timestamp authority at {authority} returned a response that is not a valid RFC 3161 token.", ex);
        }

        signerInfo.AddUnsignedAttribute(
            new AsnEncodedData(new Oid(TimestampAttributeOid), token.AsSignedCms().Encode()));

        logger?.LogInformation("Signature timestamped by {Authority} at {When}.",
            authority, token.TokenInfo.Timestamp);
    }

    /// <summary>
    /// Loads the PKCS#12 bundle and insists it carries a usable private key — a certificate
    /// without one produces a signature that cannot be computed, and failing here says so
    /// plainly rather than throwing something opaque out of the CMS layer.
    /// </summary>
    private static X509Certificate2 LoadSigningCertificate(byte[] pkcs12, string password)
    {
        X509Certificate2 certificate;
        try
        {
            // `EphemeralKeySet` would be preferable — it keeps the private key out of any
            // on-disk key store — but macOS does not support it for PKCS#12 import and
            // throws PlatformNotSupportedException. `Exportable` alone works everywhere,
            // and the certificate is disposed as soon as the signature is computed.
            certificate = new X509Certificate2(pkcs12, password, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            // Deliberately broad: the failure modes here are a wrong password
            // (CryptographicException), a file that is not PKCS#12, and platform key-store
            // restrictions — all of which are the caller's input problem, and all of which
            // must come back as a clear 400 rather than a 500 with a crypto stack trace.
            throw new InvalidOperationException(
                "The signing certificate could not be opened. Check that it is a PKCS#12 (.pfx/.p12) bundle and that the password is correct.", ex);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "The signing certificate has no private key. A .cer or .crt file cannot sign — export the certificate WITH its key as PKCS#12 (.pfx).");
        }

        return certificate;
    }

    // --- T2-4: split / rotate / flatten / N-up ------------------------------------

    /// <summary>
    /// Mechanical page operations on an already-rendered PDF.
    ///
    /// Merge proved the plumbing; these are the other four operations document assembly
    /// needs. Every one of them opens the source with PdfSharpCore, so a source the library
    /// cannot parse fails as a validation error rather than a 500.
    /// </summary>
    public Task<Result<byte[]>> TransformAsync(TransformPdfCommand command, CancellationToken cancellationToken = default)
    {
        byte[] source;
        try
        {
            source = Convert.FromBase64String(command.File ?? string.Empty);
        }
        catch (FormatException)
        {
            return Task.FromResult(Result<byte[]>.Fail(
                Error.Validation("The source 'file' is not valid base64.")));
        }

        if (source.Length == 0)
        {
            return Task.FromResult(Result<byte[]>.Fail(Error.Validation("The source 'file' is empty.")));
        }

        try
        {
            var operation = (command.Operation ?? string.Empty).Trim().ToLowerInvariant();
            var result = operation switch
            {
                "extract" => ExtractPages(source, command.Pages),
                "rotate" => RotatePages(source, command.Rotation, command.Pages),
                "flatten" => FlattenDocument(source),
                "nup" => ArrangeNUp(source, command.PagesPerSheet),
                _ => throw new InvalidOperationException($"Unsupported operation '{command.Operation}'.")
            };

            _logger.LogInformation("Transform {Operation} produced {Bytes} bytes for {Document}.",
                operation, result.Length, command.DocumentName);
            return Task.FromResult(Result<byte[]>.Success(result));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(Result<byte[]>.Fail(Error.Validation(ex.Message)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF transform failed for {Document}.", command.DocumentName);
            return Task.FromResult(Result<byte[]>.Fail(
                Error.Validation("The source could not be read as a PDF. Check that 'file' is a complete, unencrypted PDF.")));
        }
    }

    /// <summary>
    /// Parses a page selection like "1-3,7,9-" into 1-based page numbers, in the order the
    /// caller wrote them.
    ///
    /// Order is preserved deliberately: "3,1" is a reordering request, and silently sorting
    /// it would quietly refuse to do what was asked. Duplicates are kept for the same
    /// reason — "1,1" duplicates a page, which is a legitimate assembly operation.
    /// </summary>
    internal static List<int> ParsePageSelection(string? selection, int pageCount)
    {
        var pages = new List<int>();
        if (string.IsNullOrWhiteSpace(selection))
        {
            for (var i = 1; i <= pageCount; i++) pages.Add(i);
            return pages;
        }

        foreach (var part in selection.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.Length == 0) continue;

            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(token, out var single) && single >= 1 && single <= pageCount)
                    pages.Add(single);
                continue;
            }

            var fromText = token[..dash].Trim();
            var toText = token[(dash + 1)..].Trim();
            var from = fromText.Length == 0 ? 1 : (int.TryParse(fromText, out var f) ? f : 1);
            var to = toText.Length == 0 ? pageCount : (int.TryParse(toText, out var t) ? t : pageCount);

            from = Math.Max(1, from);
            to = Math.Min(pageCount, to);
            if (from > to) continue;
            for (var i = from; i <= to; i++) pages.Add(i);
        }

        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"The page selection '{selection}' matched no pages — the document has {pageCount}.");
        }
        return pages;
    }

    private static byte[] ExtractPages(byte[] source, string? selection)
    {
        using var input = new MemoryStream(source);
        using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
            input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);

        var pages = ParsePageSelection(selection, document.PageCount);
        using var target = new PdfSharpCore.Pdf.PdfDocument();
        foreach (var number in pages) target.AddPage(document.Pages[number - 1]);

        using var output = new MemoryStream();
        target.Save(output);
        return output.ToArray();
    }

    private static byte[] RotatePages(byte[] source, int degrees, string? selection)
    {
        using var input = new MemoryStream(source);
        using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
            input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

        var pages = new HashSet<int>(ParsePageSelection(selection, document.PageCount));
        foreach (var number in pages)
        {
            var page = document.Pages[number - 1];
            // Additive, and normalised: /Rotate is defined modulo 360, and a page that
            // already carried a rotation must end up turned by the amount asked for rather
            // than reset to it.
            page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// Removes the document's interactive layer: annotations and the form dictionary.
    ///
    /// Stated precisely, because "flatten" means different things to different tools: this
    /// removes interactivity, it does not rasterise the page. Text stays text and vectors
    /// stay vectors — which is what a caller flattening before archiving or emailing wants,
    /// and it keeps the file searchable. A form field's VALUE lives in its appearance
    /// stream, which is page content and is therefore kept; what goes is the ability to
    /// edit it.
    /// </summary>
    private static byte[] FlattenDocument(byte[] source)
    {
        using var input = new MemoryStream(source);
        using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
            input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            if (page.Elements.ContainsKey("/Annots")) page.Elements.Remove("/Annots");
        }
        if (document.Internals.Catalog.Elements.ContainsKey("/AcroForm"))
            document.Internals.Catalog.Elements.Remove("/AcroForm");

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// Places several source pages on each output sheet.
    ///
    /// The sheet keeps the source's paper size but flips orientation for the layouts whose
    /// grid is wider than it is tall, so two portrait pages land side by side on a
    /// landscape sheet rather than squeezed into a portrait one.
    /// </summary>
    private static byte[] ArrangeNUp(byte[] source, int pagesPerSheet)
    {
        var (columns, rows) = pagesPerSheet switch
        {
            2 => (2, 1),
            4 => (2, 2),
            6 => (3, 2),
            8 => (4, 2),
            9 => (3, 3),
            16 => (4, 4),
            _ => throw new InvalidOperationException($"Unsupported N-up layout: {pagesPerSheet}.")
        };

        using var probeInput = new MemoryStream(source);
        using var probe = PdfSharpCore.Pdf.IO.PdfReader.Open(
            probeInput, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
        var pageCount = probe.PageCount;
        if (pageCount == 0) throw new InvalidOperationException("The source PDF has no pages.");

        var sourceWidth = probe.Pages[0].Width.Point;
        var sourceHeight = probe.Pages[0].Height.Point;

        // A wider-than-tall grid wants a wider-than-tall sheet.
        var sheetWidth = columns >= rows ? Math.Max(sourceWidth, sourceHeight) : Math.Min(sourceWidth, sourceHeight);
        var sheetHeight = columns >= rows ? Math.Min(sourceWidth, sourceHeight) : Math.Max(sourceWidth, sourceHeight);
        if (columns == rows) { sheetWidth = sourceWidth; sheetHeight = sourceHeight; }

        // XPdfForm reads from a file, so the source is staged on disk for the duration.
        var temp = Path.Combine(Path.GetTempPath(), $"pdfengine-nup-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(temp, source);

            using var target = new PdfSharpCore.Pdf.PdfDocument();
            var cellWidth = sheetWidth / columns;
            var cellHeight = sheetHeight / rows;

            for (var start = 0; start < pageCount; start += pagesPerSheet)
            {
                var sheet = target.AddPage();
                sheet.Width = PdfSharpCore.Drawing.XUnit.FromPoint(sheetWidth);
                sheet.Height = PdfSharpCore.Drawing.XUnit.FromPoint(sheetHeight);

                using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(sheet);
                for (var slot = 0; slot < pagesPerSheet && start + slot < pageCount; slot++)
                {
                    using var form = PdfSharpCore.Drawing.XPdfForm.FromFile(temp);
                    form.PageIndex = start + slot;

                    // Fitted, never stretched: a source page whose aspect differs from the
                    // cell is centred in it rather than distorted.
                    var scale = Math.Min(cellWidth / form.PointWidth, cellHeight / form.PointHeight);
                    var drawWidth = form.PointWidth * scale;
                    var drawHeight = form.PointHeight * scale;
                    var x = (slot % columns) * cellWidth + (cellWidth - drawWidth) / 2;
                    var y = (slot / columns) * cellHeight + (cellHeight - drawHeight) / 2;

                    gfx.DrawImage(form, x, y, drawWidth, drawHeight);
                }
            }

            using var output = new MemoryStream();
            target.Save(output);
            return output.ToArray();
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { /* temp cleanup is best effort */ }
        }
    }



    // --- T2-2 (B-LT): document security store -------------------------------------

    /// <summary>
    /// Appends a Document Security Store carrying the material needed to validate the
    /// signature long after the fact — the certificate chain and the CRLs that say those
    /// certificates were not revoked.
    ///
    /// This is what separates PAdES B-T from B-LT. A timestamped signature proves WHEN it
    /// was made; it still requires a verifier to reach the issuing CA years later to learn
    /// whether the certificate had been revoked. Embedding that evidence makes the document
    /// self-contained, which is the entire point for archival.
    ///
    /// Written as an INCREMENTAL UPDATE — the original bytes are left untouched and new
    /// objects are appended with their own cross-reference section pointing back at the
    /// previous one. Re-saving the document through a PDF library instead would rewrite the
    /// bytes the signature seals and destroy it. That is also why this is hand-written
    /// rather than delegated: no library here can append without rewriting.
    /// </summary>
    private static byte[] ApplyDocumentSecurityStore(
        byte[] signedBytes, IEnumerable<X509Certificate2> certificates,
        ILogger? logger, List<string>? diagnosticWarnings)
    {
        var text = Encoding.Latin1.GetString(signedBytes);

        var rootMatch = Regex.Matches(text, @"/Root\s+(\d+)\s+\d+\s+R").LastOrDefault();
        var sizeMatch = Regex.Matches(text, @"/Size\s+(\d+)").LastOrDefault();
        var startMatch = Regex.Matches(text, @"startxref\s+(\d+)").LastOrDefault();
        if (rootMatch == null || sizeMatch == null || startMatch == null)
        {
            throw new InvalidOperationException(
                "The signed document's trailer could not be read, so validation data could not be appended.");
        }

        var catalogNumber = int.Parse(rootMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var nextObject = int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var previousXref = long.Parse(startMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        // The catalog is rewritten with a /DSS added. Its existing contents are reused
        // verbatim rather than rebuilt, so nothing else about the document changes.
        var catalogMatch = Regex.Match(text, $@"(?<![0-9]){catalogNumber}\s+0\s+obj\s*<<(?<body>.*?)>>\s*endobj",
            RegexOptions.Singleline);
        if (!catalogMatch.Success)
        {
            throw new InvalidOperationException(
                $"The document catalog (object {catalogNumber}) could not be located, so validation data could not be appended.");
        }
        var catalogBody = catalogMatch.Groups["body"].Value;

        var distinctCerts = certificates
            .Where(c => c != null)
            .GroupBy(c => c.Thumbprint)
            .Select(g => g.First())
            .ToList();
        if (distinctCerts.Count == 0) return signedBytes;

        var crls = DownloadRevocationLists(distinctCerts, logger);

        var appended = new StringBuilder();
        var offsets = new List<(int Number, long Offset)>();
        var baseLength = (long)signedBytes.Length;

        // A byte the file does not end with, so the appended section starts on its own line.
        appended.Append('\n');

        long CurrentOffset() => baseLength + appended.Length;

        var certRefs = new List<int>();
        foreach (var certificate in distinctCerts)
        {
            var raw = certificate.RawData;
            offsets.Add((nextObject, CurrentOffset()));
            appended.Append(nextObject).Append(" 0 obj\n<< /Length ").Append(raw.Length).Append(" >>\nstream\n")
                    .Append(Encoding.Latin1.GetString(raw)).Append("\nendstream\nendobj\n");
            certRefs.Add(nextObject);
            nextObject++;
        }

        var crlRefs = new List<int>();
        foreach (var crl in crls)
        {
            offsets.Add((nextObject, CurrentOffset()));
            appended.Append(nextObject).Append(" 0 obj\n<< /Length ").Append(crl.Length).Append(" >>\nstream\n")
                    .Append(Encoding.Latin1.GetString(crl)).Append("\nendstream\nendobj\n");
            crlRefs.Add(nextObject);
            nextObject++;
        }

        var dssNumber = nextObject++;
        offsets.Add((dssNumber, CurrentOffset()));
        appended.Append(dssNumber).Append(" 0 obj\n<< /Type /DSS");
        if (certRefs.Count > 0)
            appended.Append(" /Certs [").Append(string.Join(" ", certRefs.Select(r => $"{r} 0 R"))).Append(']');
        if (crlRefs.Count > 0)
            appended.Append(" /CRLs [").Append(string.Join(" ", crlRefs.Select(r => $"{r} 0 R"))).Append(']');
        appended.Append(" >>\nendobj\n");

        offsets.Add((catalogNumber, CurrentOffset()));
        appended.Append(catalogNumber).Append(" 0 obj\n<<").Append(catalogBody)
                .Append(" /DSS ").Append(dssNumber).Append(" 0 R >>\nendobj\n");

        // Cross-reference section. Entries are exactly 20 bytes each, and the subsections
        // must be ordered by object number — a reader that finds them out of order rejects
        // the file.
        var xrefOffset = CurrentOffset();
        appended.Append("xref\n");
        foreach (var group in GroupConsecutive(offsets.OrderBy(o => o.Number).ToList()))
        {
            appended.Append(group[0].Number).Append(' ').Append(group.Count).Append('\n');
            foreach (var entry in group)
            {
                appended.Append(entry.Offset.ToString("D10", CultureInfo.InvariantCulture))
                        .Append(" 00000 n\r\n");
            }
        }

        appended.Append("trailer\n<< /Size ").Append(nextObject)
                .Append(" /Root ").Append(catalogNumber).Append(" 0 R /Prev ").Append(previousXref)
                .Append(" >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        var result = new byte[signedBytes.Length + appended.Length];
        Buffer.BlockCopy(signedBytes, 0, result, 0, signedBytes.Length);
        var appendedBytes = Encoding.Latin1.GetBytes(appended.ToString());
        Buffer.BlockCopy(appendedBytes, 0, result, signedBytes.Length, appendedBytes.Length);

        logger?.LogInformation("Embedded validation data: {Certs} certificate(s), {Crls} CRL(s).",
            distinctCerts.Count, crls.Count);
        diagnosticWarnings?.Add(crls.Count > 0
            ? $"Signature notice: validation data embedded ({distinctCerts.Count} certificate(s), {crls.Count} CRL(s)) — the signature can be checked without reaching the issuing authority. This is PAdES B-LT."
            : $"Signature notice: {distinctCerts.Count} certificate(s) embedded, but NO certificate revocation list could be retrieved, so a verifier must still reach the issuing authority. That is short of PAdES B-LT — it usually means the certificate carries no CRL distribution point.");

        return result;
    }

    /// <summary>Groups xref entries into consecutive runs, which is what a subsection is.</summary>
    private static List<List<(int Number, long Offset)>> GroupConsecutive(List<(int Number, long Offset)> entries)
    {
        var groups = new List<List<(int Number, long Offset)>>();
        foreach (var entry in entries)
        {
            if (groups.Count > 0 && groups[^1][^1].Number + 1 == entry.Number) groups[^1].Add(entry);
            else groups.Add(new List<(int, long)> { entry });
        }
        return groups;
    }

    /// <summary>
    /// Fetches each certificate's revocation list from the CRL distribution point it
    /// advertises.
    ///
    /// Best effort by design: a certificate with no distribution point, or an unreachable
    /// one, is reported through the caller's diagnostics rather than failing the signature.
    /// The signature itself is already complete and valid at this point — what is at stake
    /// is only whether a verifier will need network access years from now.
    /// </summary>
    private static List<byte[]> DownloadRevocationLists(
        IEnumerable<X509Certificate2> certificates, ILogger? logger)
    {
        const string CrlDistributionPointOid = "2.5.29.31";
        var lists = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        foreach (var certificate in certificates)
        {
            var extension = certificate.Extensions[CrlDistributionPointOid];
            if (extension == null) continue;

            // The distribution points are IA5Strings inside the DER. Pulling the URLs out
            // textually avoids hand-rolling an ASN.1 parser for one extension.
            foreach (Match match in Regex.Matches(
                         Encoding.Latin1.GetString(extension.RawData), @"https?://[^\s\x00-\x1f<>""]+"))
            {
                var url = match.Value.TrimEnd('.', ',', ')');
                if (!seen.Add(url)) continue;
                try
                {
                    var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    // A PEM-armoured list is converted; a DER one is already what is wanted.
                    lists.Add(bytes.Length > 0 && bytes[0] == 0x30 ? bytes : ConvertPemToDer(bytes));
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("Could not fetch the revocation list at {Url}: {Message}", url, ex.Message);
                }
            }
        }

        return lists.Where(l => l.Length > 0).ToList();
    }

    private static byte[] ConvertPemToDer(byte[] pem)
    {
        try
        {
            var text = Encoding.ASCII.GetString(pem);
            var body = Regex.Replace(text, "-----(BEGIN|END)[^-]*-----", string.Empty);
            return Convert.FromBase64String(Regex.Replace(body, @"\s+", string.Empty));
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    // --- T2-3: interactive form fields --------------------------------------------

    /// <summary>
    /// Adds fillable form fields to the finished document.
    ///
    /// The backlog had this blocked on the library, which was the wrong conclusion drawn
    /// from a true observation: `PdfTextField` really does have zero public constructors in
    /// both PdfSharpCore and PDFsharp — but a form field is not a library type, it is a
    /// widget annotation on a page plus an entry in the catalog's AcroForm, and both are
    /// ordinary dictionaries this engine already writes by hand for attachments. What was
    /// missing was a convenience API, not the capability.
    /// </summary>
    private static void ApplyFormFields(
        PdfSharpCore.Pdf.PdfDocument document, RenderingOptions options,
        ILogger? logger, List<string>? diagnosticWarnings)
    {
        var fields = new PdfSharpCore.Pdf.PdfArray(document);
        var placed = 0;
        var skipped = new List<string>();

        // The base-14 faces the appearance streams below draw with. Neither needs
        // embedding, which is also precisely why a form cannot be PDF/A — archival
        // conformance requires every font embedded.
        var helvetica = Base14(document, "/Helvetica", "/WinAnsiEncoding");
        var dingbats = Base14(document, "/ZapfDingbats", null);

        foreach (var field in options.FormFields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                skipped.Add("(unnamed)");
                continue;
            }
            if (field.Page < 1 || field.Page > document.PageCount)
            {
                skipped.Add($"{field.Name} (page {field.Page} does not exist)");
                continue;
            }

            var page = document.Pages[field.Page - 1];
            var widget = new PdfSharpCore.Pdf.PdfDictionary(document);
            document.Internals.AddObject(widget);

            // A widget annotation and the field itself are the same dictionary here, which
            // is the normal shape for a field with exactly one appearance on one page.
            widget.Elements["/Type"] = new PdfSharpCore.Pdf.PdfName("/Annot");
            widget.Elements["/Subtype"] = new PdfSharpCore.Pdf.PdfName("/Widget");
            widget.Elements["/T"] = new PdfSharpCore.Pdf.PdfString(field.Name);
            widget.Elements["/P"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(page);

            // PDF places the origin at the BOTTOM-left; the API takes coordinates from the
            // top-left, because that is how every other measurement in this engine is
            // expressed and callers should not have to flip axes.
            var top = page.Height.Point - field.Y;
            var bottom = top - Math.Max(1, field.Height);
            var rect = new PdfSharpCore.Pdf.PdfArray(document);
            foreach (var value in new[] { field.X, bottom, field.X + Math.Max(1, field.Width), top })
                rect.Elements.Add(new PdfSharpCore.Pdf.PdfReal(Math.Round(value, 2)));
            widget.Elements["/Rect"] = rect;

            // Bit 3 = Print. Without it the field shows on screen and vanishes on paper.
            widget.Elements["/F"] = new PdfSharpCore.Pdf.PdfInteger(4);

            // Field flags: bit 1 ReadOnly, bit 2 Required.
            var flags = (field.ReadOnly ? 1 : 0) | (field.Required ? 2 : 0);
            if (flags != 0) widget.Elements["/Ff"] = new PdfSharpCore.Pdf.PdfInteger(flags);

            if (!string.IsNullOrWhiteSpace(field.ToolTip))
                widget.Elements["/TU"] = new PdfSharpCore.Pdf.PdfString(field.ToolTip);

            switch ((field.Type ?? "text").Trim().ToLowerInvariant())
            {
                case "checkbox":
                    widget.Elements["/FT"] = new PdfSharpCore.Pdf.PdfName("/Btn");
                    var ticked = (field.Value ?? string.Empty).Trim().ToLowerInvariant()
                        is "true" or "on" or "yes" or "1";
                    var state = new PdfSharpCore.Pdf.PdfName(ticked ? "/Yes" : "/Off");
                    widget.Elements["/V"] = state;
                    widget.Elements["/AS"] = state;
                    // A reader that honours NeedAppearances REGENERATES this appearance,
                    // and needs to be told which font the tick is drawn in — otherwise it
                    // resolves /ZaDb against /DR, does not find it, and warns.
                    widget.Elements["/DA"] = new PdfSharpCore.Pdf.PdfString("/ZaDb 0 Tf 0 g");
                    AddFieldDecoration(document, widget);
                    widget.Elements["/AP"] = CheckBoxAppearance(
                        document, dingbats, Math.Max(1, field.Width), Math.Max(1, field.Height));
                    break;

                default:
                    widget.Elements["/FT"] = new PdfSharpCore.Pdf.PdfName("/Tx");
                    if (!string.IsNullOrEmpty(field.Value))
                        widget.Elements["/V"] = new PdfSharpCore.Pdf.PdfString(field.Value);
                    // The default appearance string the reader uses to draw the text.
                    var size = Math.Clamp(field.FontSize <= 0 ? 10 : field.FontSize, 4, 72);
                    widget.Elements["/DA"] = new PdfSharpCore.Pdf.PdfString(
                        $"/Helv {size.ToString("0.##", CultureInfo.InvariantCulture)} Tf 0 g");
                    AddFieldDecoration(document, widget);
                    widget.Elements["/AP"] = TextAppearance(
                        document, helvetica,
                        Math.Max(1, field.Width), Math.Max(1, field.Height), size, field.Value);
                    break;
            }

            // The widget has to be reachable from its page as well as from the form, or it
            // is a field the reader knows about and never draws.
            var annots = page.Elements.GetArray("/Annots");
            if (annots == null)
            {
                annots = new PdfSharpCore.Pdf.PdfArray(document);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(widget));

            fields.Elements.Add(PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(widget));
            placed++;
        }

        if (placed == 0)
        {
            if (skipped.Count > 0)
            {
                diagnosticWarnings?.Add(
                    $"Form warning: {skipped.Count} form field(s) were skipped and none were added: {string.Join(", ", skipped.Take(4))}.");
            }
            return;
        }

        var fontResources = new PdfSharpCore.Pdf.PdfDictionary(document);
        fontResources.Elements["/Helv"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(helvetica);
        fontResources.Elements["/ZaDb"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(dingbats);
        var resources = new PdfSharpCore.Pdf.PdfDictionary(document);
        resources.Elements["/Font"] = fontResources;

        var acroForm = new PdfSharpCore.Pdf.PdfDictionary(document);
        document.Internals.AddObject(acroForm);
        acroForm.Elements["/Fields"] = fields;
        acroForm.Elements["/DR"] = resources;
        acroForm.Elements["/DA"] = new PdfSharpCore.Pdf.PdfString("/Helv 10 Tf 0 g");
        // Every widget above also carries a real /AP, because NeedAppearances is a REQUEST
        // and several viewers — macOS Preview among them — ignore it. A field with no
        // appearance stream draws nothing at all there: the form is present in the object
        // tree, invisible on the page, and the user reasonably concludes it does not work.
        // NeedAppearances stays on so readers that DO honour it regenerate after filling.
        acroForm.Elements["/NeedAppearances"] = new PdfSharpCore.Pdf.PdfBoolean(true);
        document.Internals.Catalog.Elements["/AcroForm"] =
            PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(acroForm);

        if (skipped.Count > 0)
        {
            diagnosticWarnings?.Add(
                $"Form warning: {placed} form field(s) were added and {skipped.Count} skipped: {string.Join(", ", skipped.Take(4))}.");
        }
        logger?.LogInformation("Added {Count} interactive form field(s).", placed);
    }

    private static PdfSharpCore.Pdf.PdfDictionary Base14(
        PdfSharpCore.Pdf.PdfDocument document, string baseFont, string? encoding)
    {
        var font = new PdfSharpCore.Pdf.PdfDictionary(document);
        document.Internals.AddObject(font);
        font.Elements["/Type"] = new PdfSharpCore.Pdf.PdfName("/Font");
        font.Elements["/Subtype"] = new PdfSharpCore.Pdf.PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfSharpCore.Pdf.PdfName(baseFont);
        if (encoding != null) font.Elements["/Encoding"] = new PdfSharpCore.Pdf.PdfName(encoding);
        return font;
    }

    /// <summary>A visible border and background, so the field reads as a field.</summary>
    private static void AddFieldDecoration(
        PdfSharpCore.Pdf.PdfDocument document, PdfSharpCore.Pdf.PdfDictionary widget)
    {
        var mk = new PdfSharpCore.Pdf.PdfDictionary(document);
        var border = new PdfSharpCore.Pdf.PdfArray(document);
        foreach (var c in new[] { 0.45, 0.47, 0.55 }) border.Elements.Add(new PdfSharpCore.Pdf.PdfReal(c));
        var background = new PdfSharpCore.Pdf.PdfArray(document);
        foreach (var c in new[] { 0.97, 0.97, 0.99 }) background.Elements.Add(new PdfSharpCore.Pdf.PdfReal(c));
        mk.Elements["/BC"] = border;
        mk.Elements["/BG"] = background;
        widget.Elements["/MK"] = mk;

        var bs = new PdfSharpCore.Pdf.PdfDictionary(document);
        bs.Elements["/W"] = new PdfSharpCore.Pdf.PdfInteger(1);
        bs.Elements["/S"] = new PdfSharpCore.Pdf.PdfName("/S");
        widget.Elements["/BS"] = bs;
    }

    private static PdfSharpCore.Pdf.PdfDictionary AppearanceStream(
        PdfSharpCore.Pdf.PdfDocument document, PdfSharpCore.Pdf.PdfDictionary font,
        string fontName, double width, double height, string content)
    {
        var form = new PdfSharpCore.Pdf.PdfDictionary(document);
        document.Internals.AddObject(form);
        form.Elements["/Type"] = new PdfSharpCore.Pdf.PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfSharpCore.Pdf.PdfName("/Form");
        form.Elements["/FormType"] = new PdfSharpCore.Pdf.PdfInteger(1);
        var bbox = new PdfSharpCore.Pdf.PdfArray(document);
        foreach (var v in new[] { 0, 0, width, height })
            bbox.Elements.Add(new PdfSharpCore.Pdf.PdfReal(Math.Round(v, 2)));
        form.Elements["/BBox"] = bbox;

        var fonts = new PdfSharpCore.Pdf.PdfDictionary(document);
        fonts.Elements[fontName] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(font);
        var resources = new PdfSharpCore.Pdf.PdfDictionary(document);
        resources.Elements["/Font"] = fonts;
        form.Elements["/Resources"] = resources;

        form.CreateStream(Encoding.ASCII.GetBytes(content));
        return form;
    }

    private static PdfSharpCore.Pdf.PdfDictionary TextAppearance(
        PdfSharpCore.Pdf.PdfDocument document, PdfSharpCore.Pdf.PdfDictionary helvetica,
        double width, double height, double size, string? value)
    {
        var inv = CultureInfo.InvariantCulture;
        var w = width.ToString("0.##", inv);
        var h = height.ToString("0.##", inv);
        var body = new StringBuilder();
        body.Append("0.97 0.97 0.99 rg 0 0 ").Append(w).Append(' ').Append(h).Append(" re f\n");
        body.Append("0.45 0.47 0.55 RG 0.5 w 0.5 0.5 ")
            .Append((width - 1).ToString("0.##", inv)).Append(' ')
            .Append((height - 1).ToString("0.##", inv)).Append(" re S\n");
        // /Tx BMC ... EMC is what marks the variable-text region a reader replaces when
        // the value changes. Without it a filled value can end up drawn twice.
        body.Append("/Tx BMC\nq\nBT\n");
        if (!string.IsNullOrEmpty(value))
        {
            var baseline = Math.Max(2, (height - size) / 2 + size * 0.18);
            body.Append("/Helv ").Append(size.ToString("0.##", inv)).Append(" Tf 0 g\n")
                .Append("2 ").Append(baseline.ToString("0.##", inv)).Append(" Td (")
                .Append(EscapePdfLiteral(value)).Append(") Tj\n");
        }
        body.Append("ET\nQ\nEMC\n");

        var ap = new PdfSharpCore.Pdf.PdfDictionary(document);
        ap.Elements["/N"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(
            AppearanceStream(document, helvetica, "/Helv", width, height, body.ToString()));
        return ap;
    }

    private static PdfSharpCore.Pdf.PdfDictionary CheckBoxAppearance(
        PdfSharpCore.Pdf.PdfDocument document, PdfSharpCore.Pdf.PdfDictionary dingbats,
        double width, double height)
    {
        var inv = CultureInfo.InvariantCulture;
        var w = width.ToString("0.##", inv);
        var h = height.ToString("0.##", inv);
        var box = "0.97 0.97 0.99 rg 0 0 " + w + " " + h + " re f\n" +
                  "0.45 0.47 0.55 RG 0.6 w 0.3 0.3 " +
                  (width - 0.6).ToString("0.##", inv) + " " +
                  (height - 0.6).ToString("0.##", inv) + " re S\n";
        // ZapfDingbats 'a20' (char 4) is the check mark every reader draws for a tick.
        var size = Math.Min(width, height) * 0.78;
        var tick = box + "q\nBT\n/ZaDb " + size.ToString("0.##", inv) + " Tf 0 g\n" +
                   (width * 0.18).ToString("0.##", inv) + " " +
                   (height * 0.22).ToString("0.##", inv) + " Td (4) Tj\nET\nQ\n";

        var ap = new PdfSharpCore.Pdf.PdfDictionary(document);
        // BOTH states are required: a checkbox with only /Yes cannot be un-ticked, and a
        // reader that resolves /AS to a missing state draws nothing at all.
        ap.Elements["/N"] = BuildStates(document, dingbats, width, height, tick, box);
        return ap;
    }

    private static PdfSharpCore.Pdf.PdfDictionary BuildStates(
        PdfSharpCore.Pdf.PdfDocument document, PdfSharpCore.Pdf.PdfDictionary dingbats,
        double width, double height, string onContent, string offContent)
    {
        var states = new PdfSharpCore.Pdf.PdfDictionary(document);
        states.Elements["/Yes"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(
            AppearanceStream(document, dingbats, "/ZaDb", width, height, onContent));
        states.Elements["/Off"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(
            AppearanceStream(document, dingbats, "/ZaDb", width, height, offContent));
        return states;
    }

    private static string EscapePdfLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    // --- T2-1: attachments / embedded files --------------------------------------

    /// <summary>
    /// Embeds the caller's files in the document as PDF/A-3 associated files.
    ///
    /// This is what makes EU e-invoicing possible: Factur-X and ZUGFeRD are a PDF/A-3
    /// invoice with the machine-readable XML embedded inside it, so one file serves both a
    /// human reader and the buyer's accounting system.
    ///
    /// Each file needs three things, and a reader that finds only two of them shows
    /// nothing: the bytes as an <c>/EmbeddedFile</c> stream, a <c>/Filespec</c> naming it,
    /// and that filespec registered BOTH in the catalog's <c>/Names/EmbeddedFiles</c> name
    /// tree (which is what the attachment pane lists) and in the catalog's <c>/AF</c> array
    /// (which is what makes it an *associated* file and is required by PDF/A-3).
    /// </summary>
    private static void ApplyAttachments(
        PdfSharpCore.Pdf.PdfDocument document, RenderingOptions options,
        ILogger? logger, List<string>? diagnosticWarnings)
    {
        var embedded = new List<PdfSharpCore.Pdf.PdfDictionary>();
        var names = new PdfSharpCore.Pdf.PdfArray(document);
        var skipped = 0;

        foreach (var attachment in options.Attachments)
        {
            if (string.IsNullOrWhiteSpace(attachment.FileName)
                || string.IsNullOrWhiteSpace(attachment.ContentBase64))
            {
                skipped++;
                continue;
            }

            byte[] payload;
            try { payload = Convert.FromBase64String(attachment.ContentBase64); }
            catch (FormatException)
            {
                skipped++;
                logger?.LogWarning("Attachment {Name} is not valid base64 and was skipped.", attachment.FileName);
                continue;
            }

            var stream = new PdfSharpCore.Pdf.Advanced.PdfEmbeddedFile(document, payload, null!);
            document.Internals.AddObject(stream);
            stream.Elements["/Type"] = new PdfSharpCore.Pdf.PdfName("/EmbeddedFile");
            stream.Elements["/Subtype"] = new PdfSharpCore.Pdf.PdfName(EncodePdfName(attachment.MimeType));

            var parameters = new PdfSharpCore.Pdf.PdfDictionary(document);
            parameters.Elements["/Size"] = new PdfSharpCore.Pdf.PdfInteger(payload.Length);
            stream.Elements["/Params"] = parameters;

            var spec = new PdfSharpCore.Pdf.PdfDictionary(document);
            document.Internals.AddObject(spec);
            spec.Elements["/Type"] = new PdfSharpCore.Pdf.PdfName("/Filespec");
            spec.Elements["/F"] = new PdfSharpCore.Pdf.PdfString(attachment.FileName);
            spec.Elements["/UF"] = new PdfSharpCore.Pdf.PdfString(attachment.FileName);
            if (!string.IsNullOrWhiteSpace(attachment.Description))
                spec.Elements["/Desc"] = new PdfSharpCore.Pdf.PdfString(attachment.Description);
            spec.Elements["/AFRelationship"] = new PdfSharpCore.Pdf.PdfName(NormalizeRelationship(attachment.Relationship));

            var files = new PdfSharpCore.Pdf.PdfDictionary(document);
            files.Elements["/F"] = PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(stream);
            spec.Elements["/EF"] = files;

            names.Elements.Add(new PdfSharpCore.Pdf.PdfString(attachment.FileName));
            names.Elements.Add(PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(spec));
            embedded.Add(spec);
        }

        if (embedded.Count == 0)
        {
            if (skipped > 0)
            {
                diagnosticWarnings?.Add(
                    $"Attachment warning: {skipped} attachment(s) were skipped because they had no file name or their content was not valid base64. The document was produced without them.");
            }
            return;
        }

        var nameTree = new PdfSharpCore.Pdf.PdfDictionary(document);
        nameTree.Elements["/Names"] = names;
        var catalogNames = new PdfSharpCore.Pdf.PdfDictionary(document);
        catalogNames.Elements["/EmbeddedFiles"] = nameTree;
        document.Internals.Catalog.Elements["/Names"] = catalogNames;

        var associated = new PdfSharpCore.Pdf.PdfArray(document);
        foreach (var spec in embedded)
            associated.Elements.Add(PdfSharpCore.Pdf.Advanced.PdfInternals.GetReference(spec));
        document.Internals.Catalog.Elements["/AF"] = associated;

        if (skipped > 0)
        {
            diagnosticWarnings?.Add(
                $"Attachment warning: {skipped} attachment(s) were skipped because they had no file name or their content was not valid base64; {embedded.Count} were embedded.");
        }
    }

    /// <summary>
    /// The MIME type as a PDF name.
    ///
    /// Deliberately NOT pre-escaped: PdfSharpCore escapes the name itself on save, so
    /// hand-writing the `#2F` for the slash produces `/text#232Fxml` — the `#` gets escaped
    /// in turn, and the value decodes to `text#2Fxml` rather than `text/xml`. Measured:
    /// veraPDF failed PDF/A-3b clause 6.8 test 1 on exactly that, which for an e-invoice
    /// means a rejected invoice rather than a cosmetic defect.
    /// </summary>
    private static string EncodePdfName(string? value) =>
        "/" + (string.IsNullOrWhiteSpace(value) ? "application/octet-stream" : value.Trim());

    /// <summary>
    /// Only the five relationships the spec defines are accepted. A Factur-X reader looks
    /// for `Data` specifically, so an unrecognised value degrades to `Unspecified` rather
    /// than being passed through to produce an attachment nothing will look at.
    /// </summary>
    private static string NormalizeRelationship(string? relationship) =>
        (relationship ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "data" => "/Data",
            "source" => "/Source",
            "alternative" => "/Alternative",
            "supplement" => "/Supplement",
            _ => "/Unspecified"
        };

    // --- T1-9: per-page reservation ----------------------------------------------

    /// <summary>
    /// The first content on a page that would be overrun by a band of the given height,
    /// expressed as the text to look for rather than as a coordinate.
    ///
    /// Words are taken in reading order and the first one whose BOTTOM edge dips into the
    /// band is the cut point. Using the bottom edge and not the top matters: a word whose
    /// top clears the band can still have its descender inside it, and a band drawn over
    /// a row of descenders is exactly the overlap this is meant to prevent.
    /// </summary>
    private static (string Needle, string ShortNeedle)? FindBandBreakAnchor(
        UglyToad.PdfPig.Content.Page pdfPage, double bandTopY)
    {
        var words = pdfPage.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (words.Count == 0) return null;

        // Reading order: lines top-first, then left to right within a line.
        //
        // Lines are grouped by BASELINE, not by the word's top or bottom edge. A word's
        // bounding box tracks its actual glyphs, so on one line "alpha" and "juliet" have
        // tops several points apart and bottoms several points apart — bucketing on either
        // split single lines across buckets and interleaved the reading order, which
        // produced anchor text that appears nowhere in the document. The baseline is the
        // one value every word on a line shares.
        static double BaselineOf(UglyToad.PdfPig.Content.Word word) =>
            word.Letters.Count > 0 ? word.Letters[0].StartBaseLine.Y : word.BoundingBox.Bottom;

        var ordered = words
            .OrderByDescending(w => Math.Round(BaselineOf(w) / 2.0))
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var index = ordered.FindIndex(w => w.BoundingBox.Bottom < bandTopY);

        // index 0 means the page's very first word is already inside the band: there is
        // nothing left to push, and breaking would produce an empty page.
        if (index <= 0) return null;

        return (string.Join(" ", ordered.Skip(index).Take(8).Select(w => w.Text)),
                string.Join(" ", ordered.Skip(index).Take(3).Select(w => w.Text)));
    }

    /// <summary>
    /// Forces a page break before whichever block holds the anchor text, clearing the
    /// bottom of that page for its band.
    ///
    /// The anchor comes from the rendered PDF, so it is found in the DOM by TEXT rather
    /// than by geometry: DOM scroll coordinates are non-linear with respect to real page
    /// boundaries as soon as any forced break exists, which is the same reason page
    /// numbers are never estimated from the DOM either.
    ///
    /// Returns how full the page was left, as a fraction — the caller uses it to refuse a
    /// break that would stranded most of a page to save a few points of band.
    /// </summary>
    private static async Task<double> ApplyPerPageBreakAsync(
        IPage page, string needle, string shortNeedle)
    {
        return await page.EvaluateAsync<double>(@"(args) => {
            const [rawNeedle, rawShort] = args;
            const BLOCKS = 'p,li,tr,h1,h2,h3,h4,h5,h6,blockquote,figure,pre,table,ul,ol,section,article,div';

            // Both sides are compared with ALL whitespace removed. Word boundaries are
            // exactly where the two disagree: a call marker renders as `golf1` in the
            // extracted PDF text but sits in the DOM as a separate <sup>, and joining PDF
            // words with single spaces invents gaps the DOM does not have. Squashing
            // sidesteps every one of those disagreements, and an eight-word needle stays
            // long enough that collisions are not a practical risk.
            const squash = (s) => (s || '').normalize('NFKC').replace(/\s+/g, '');

            // Hidden content must be excluded, not merely skipped over. Footnote bodies
            // are still in the DOM (display:none) sitting INSIDE the paragraph that
            // referenced them, so textContent splices a whole footnote into the middle of
            // the very sentence the anchor is trying to match.
            const cache = new WeakMap();
            const visibleText = (el) => {
                if (cache.has(el)) return cache.get(el);
                let out = '';
                for (const node of el.childNodes) {
                    if (node.nodeType === 3) { out += node.nodeValue; continue; }
                    if (node.nodeType !== 1) continue;
                    const st = window.getComputedStyle(node);
                    if (st.display === 'none' || st.visibility === 'hidden') continue;
                    out += visibleText(node);
                }
                out = squash(out);
                cache.set(el, out);
                return out;
            };

            const descend = (root, needle) => {
                let node = root;
                for (let depth = 0; depth < 64; depth++) {
                    let next = null;
                    for (const child of node.children) {
                        const st = window.getComputedStyle(child);
                        if (st.display === 'none' || st.visibility === 'hidden') continue;
                        if (visibleText(child).indexOf(needle) !== -1) { next = child; break; }
                    }
                    if (!next) return node;
                    node = next;
                }
                return node;
            };

            // A break forced on a grid/flex column fragments that one column and leaves
            // its siblings behind — reproducibly a blank page between the two. Break the
            // whole row instead.
            const escapeMultiColumn = (el) => {
                let node = el;
                for (let depth = 0; node && node.parentElement && depth < 4; depth++) {
                    const st = window.getComputedStyle(node.parentElement);
                    const grid = st.display === 'grid' || st.display === 'inline-grid';
                    const rowFlex = (st.display === 'flex' || st.display === 'inline-flex')
                        && st.flexDirection !== 'column' && st.flexDirection !== 'column-reverse';
                    if ((grid || rowFlex) && node.parentElement.children.length > 1) node = node.parentElement;
                    else break;
                }
                return node;
            };

            const needle = squash(rawNeedle);
            const shortNeedle = squash(rawShort);
            const bodyText = visibleText(document.body);

            let node = null;
            if (needle.length >= 8 && bodyText.indexOf(needle) !== -1) node = descend(document.body, needle);
            else if (shortNeedle.length >= 6 && bodyText.indexOf(shortNeedle) !== -1) node = descend(document.body, shortNeedle);
            if (!node) return -1;   // not findable in the DOM; the caller reports it

            if (shortNeedle.length >= 6 && visibleText(node).indexOf(shortNeedle) !== -1) {
                node = descend(node, shortNeedle);
            }

            let target = node.closest ? (node.closest(BLOCKS) || node) : node;
            if (!target || target === document.body || target === document.documentElement) return -1;
            target = escapeMultiColumn(target);
            if (target === document.body || target === document.documentElement) return -1;

            // Already moved by an earlier pass, or it is the very first thing in the
            // document — breaking there only produces a leading blank page.
            if (target.hasAttribute('data-pdfengine-band-break')) return -2;
            if (target === document.body.firstElementChild) return -2;

            target.style.setProperty('break-before', 'page', 'important');
            target.style.setProperty('page-break-before', 'always', 'important');
            target.setAttribute('data-pdfengine-band-break', '1');
            return 1;
        }", new object[] { needle, shortNeedle });
    }

    // --- T1-7: named pages -------------------------------------------------------

    /// <summary>
    /// The page-geometry overrides currently in force, so the run renderer and the
    /// footnote/float reflow can each change their own part without clobbering the other's.
    /// Both end up in one injected `@page` rule.
    /// </summary>
    private sealed class PageOverrideState
    {
        public double? TopMarginPt { get; set; }
        public double? BottomMarginPt { get; set; }
        public bool PlannerBreaksCleared { get; set; }
        public bool HasReservation => TopMarginPt.HasValue || BottomMarginPt.HasValue;
    }

    /// <summary>
    /// Writes the single stylesheet that carries every engine-applied page override: the
    /// current run's geometry, the reserved edge bands, and which run is visible.
    ///
    /// One element, rewritten in place, rather than a stylesheet per change — the cascade
    /// stays a two-rule question (the author's `@page`, then ours) no matter how many
    /// passes or runs a document needs.
    ///
    /// It is an injected `@page` rule and not Playwright's margin option for a measured
    /// reason: `preferCSSPageSize` defaults on, and a document that declares its own
    /// `@page { margin }` makes Chromium take margins from CSS and IGNORE the print API's
    /// margins entirely — verified, three renders at 93px/111px/129px produced
    /// byte-identical PDFs. A stylesheet appended last wins in both configurations.
    /// </summary>
    private static async Task<int> ApplyPageOverridesAsync(
        IPage page, PaginationPlan plan, PageOverrideState state, int? runIndex, bool clearPlannerBreaks)
    {
        var css = new StringBuilder();
        var runGeometry = new StringBuilder();

        if (runIndex is int index && index >= 0 && index < plan.PageRuns.Count)
        {
            var run = plan.PageRuns[index];
            if (run.Name.Length > 0 && plan.NamedPages.TryGetValue(run.Name, out var definition))
            {
                AppendNamedPageGeometry(runGeometry, definition);
            }
            // Everything that is not this run is taken out of layout, so the render
            // produces exactly this run's pages and nothing else.
            css.Append("[data-pdfengine-pagerun]:not([data-pdfengine-pagerun=\"")
               .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
               .Append("\"]) { display: none !important; }\n");
        }

        // The reservation is appended AFTER the run's own margins so it wins for the edges
        // it needs; a named page's left/right margins are untouched by it.
        var reservation = new StringBuilder();
        if (state.TopMarginPt is double top)
            reservation.Append("margin-top: ").Append(FormatPx(top)).Append("; ");
        if (state.BottomMarginPt is double bottom)
            reservation.Append("margin-bottom: ").Append(FormatPx(bottom)).Append("; ");

        if (runGeometry.Length > 0 || reservation.Length > 0)
            css.Append("@page { ").Append(runGeometry).Append(reservation).Append("}\n");

        // `@page :first` outranks a bare `@page`, so anything the engine puts in the bare
        // rule is silently overridden on the first page of every part whenever the author
        // declares `:first` geometry. Both of the engine's own concerns therefore have to
        // be restated at `:first` specificity:
        //
        //   * the reserved band, or page 1 would lose it to the author's cover margins;
        //   * the run's paper, because Chromium treats the first page of EVERY part as
        //     `:first` — and a reset that re-asserted only the DEFAULT geometry turned a
        //     one-page landscape run back to portrait, measured.
        //
        // Only parts after the first cancel the author's `:first`; on the real first page
        // of the document it is exactly what the author asked for.
        if (plan.PseudoPagesWithGeometry.Contains("first", StringComparer.OrdinalIgnoreCase))
        {
            var firstRule = new StringBuilder();
            if (runIndex is int i && i > 0 && plan.DefaultPage != null)
                AppendNamedPageGeometry(firstRule, plan.DefaultPage);
            firstRule.Append(runGeometry).Append(reservation);
            if (firstRule.Length > 0)
                css.Append("@page :first { ").Append(firstRule).Append("}\n");
        }

        if (clearPlannerBreaks)
        {
            // Safe only because the inline styles this would otherwise be outranked by
            // have just been removed. Chromium measures `break-after: avoid` against the
            // REAL page boundaries, which is strictly better information than the
            // pre-render estimate it replaces.
            css.Append("@media print { h1,h2,h3,h4,h5,h6 { break-after: avoid; page-break-after: avoid; } }\n");
        }

        return await page.EvaluateAsync<int>(@"(args) => {
            const [css, clearBreaks] = args;
            let el = document.getElementById('pdfengine-page-overrides');
            if (!el) {
                el = document.createElement('style');
                el.id = 'pdfengine-page-overrides';
                (document.head || document.documentElement).appendChild(el);
            }
            el.textContent = css;

            if (!clearBreaks) return 0;

            // Pass 2's forced breaks are estimates measured against the page height as it
            // stood BEFORE any of this. Reserving a band or switching a run to different
            // paper moves every real page boundary, and each now-stale break strands
            // whatever follows it on a page of its own — measured, two pages holding a
            // single paragraph each.
            let cleared = 0;
            document.querySelectorAll('[data-pdfengine-planner-break]').forEach(node => {
                node.style.removeProperty('break-before');
                node.style.removeProperty('page-break-before');
                node.removeAttribute('data-pdfengine-planner-break');
                cleared++;
            });
            return cleared;
        }", new object[] { css.ToString(), clearPlannerBreaks });
    }

    private static string FormatPx(double valuePt) =>
        (valuePt / 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "px";

    /// <summary>Turns a parsed `@page &lt;name&gt;` definition back into CSS declarations.</summary>
    private static void AppendNamedPageGeometry(StringBuilder target, NamedPageDefinition definition)
    {
        if (definition.Width != null && definition.Height != null)
        {
            target.Append("size: ").Append(definition.Width).Append(' ').Append(definition.Height);
            if (definition.Landscape == true) target.Append(" landscape");
            target.Append("; ");
        }
        else if (definition.PageSize != null)
        {
            target.Append("size: ").Append(definition.PageSize);
            if (definition.Landscape == true) target.Append(" landscape");
            else if (definition.Landscape == false) target.Append(" portrait");
            target.Append("; ");
        }
        else if (definition.Landscape.HasValue)
        {
            target.Append("size: ").Append(definition.Landscape.Value ? "landscape" : "portrait").Append("; ");
        }

        if (definition.MarginTop != null) target.Append("margin-top: ").Append(definition.MarginTop).Append("; ");
        if (definition.MarginRight != null) target.Append("margin-right: ").Append(definition.MarginRight).Append("; ");
        if (definition.MarginBottom != null) target.Append("margin-bottom: ").Append(definition.MarginBottom).Append("; ");
        if (definition.MarginLeft != null) target.Append("margin-left: ").Append(definition.MarginLeft).Append("; ");
    }

    /// <summary>
    /// Stitches the separately-rendered runs into one document.
    ///
    /// Verified before this was built on: a PdfSharpCore import-merge preserves each page's
    /// own geometry — an A4-landscape part, an A4-portrait part and an A5 part came back
    /// out as 842x595, 595x842 and 420x595. Without that, stitching would have silently
    /// flattened every named page back to one size and the feature would have been
    /// impossible by this route.
    /// </summary>
    private static byte[] MergePdfParts(IReadOnlyList<byte[]> parts)
    {
        if (parts.Count == 1) return parts[0];

        using var target = new PdfSharpCore.Pdf.PdfDocument();
        foreach (var part in parts)
        {
            using var input = new MemoryStream(part);
            using var source = PdfSharpCore.Pdf.IO.PdfReader.Open(
                input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            for (var i = 0; i < source.PageCount; i++) target.AddPage(source.Pages[i]);
        }

        using var output = new MemoryStream();
        target.Save(output);
        return output.ToArray();
    }

    /// <summary>
    /// Re-derives each heading's page from the stitched document.
    ///
    /// The planner's outline pages are counted during a single continuous pass over the
    /// DOM, which stops being true the moment the document is rendered in parts — every
    /// heading after the first run would point at the wrong page. Reading them back out of
    /// the finished PDF is the same read-the-real-PDF rule that governs cross-references
    /// and running headers.
    /// </summary>
    private static void RelocateOutlineForStitchedDocument(
        byte[] pdfBytes, PaginationPlan plan, ILogger? logger)
    {
        if (plan.HeadingOutline.Count == 0) return;

        var requests = plan.HeadingOutline
            .Select((h, i) => new PageRefRequest
            {
                Id = $"__heading_{i}",
                Fingerprint = h.Text,
                ShortFingerprint = h.Text.Length > 40 ? h.Text[..40] : h.Text
            })
            .ToList();

        var resolved = ResolvePageReferencesFromPdf(pdfBytes, requests, logger);
        for (var i = 0; i < plan.HeadingOutline.Count; i++)
        {
            if (resolved.TryGetValue($"__heading_{i}", out var p) && p > 0)
            {
                plan.HeadingOutline[i].Page = p;
            }
        }
    }

    // --- T1-8: page floats -------------------------------------------------------

    /// <summary>
    /// Captures each `float: top` / `float: bottom` element as an image and then takes it
    /// out of the layout.
    ///
    /// This has to run while the browser still has the elements laid out, and before the
    /// first PDF is taken, so the render the placement is measured against already
    /// excludes them. A page float is arbitrary content — a chart, a figure, a table — so
    /// unlike a footnote it cannot be redrawn from a text description; the pixels the
    /// browser produced are the only faithful representation available. The cost of that
    /// (no text layer under the float) is reported by the planner, not hidden.
    ///
    /// The capture is taken at twice the laid-out size and drawn back at the original box,
    /// so the float prints at roughly 192 DPI rather than the 96 DPI a 1:1 screenshot
    /// would give.
    /// </summary>
    private async Task CapturePageFloatsAsync(
        IPage page, PaginationPlan plan, List<string> warnings)
    {
        const int CaptureScale = 2;
        var failed = 0;

        foreach (var floated in plan.PageFloats)
        {
            try
            {
                // Captured from an off-flow CLONE rather than from the element in place.
                // Scaling the element where it sits was tried and is wrong twice over:
                // `zoom` re-lays it out, so the capture no longer has the aspect ratio of
                // the box it gets drawn into (measured — the figure came out visibly
                // squashed), and any scaling in place makes the element overlap its
                // neighbours, which then appear in the capture. A clone on a white backdrop
                // has neither problem, and `transform` scales the PAINTED result without
                // touching layout, so the proportions are exactly those of the original.
                var prepared = await page.EvaluateAsync<bool>(@"(args) => {
                    const [number, scale] = args;
                    const el = document.querySelector('[data-pdfengine-pagefloat=""' + number + '""]');
                    if (!el) return false;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 1 || rect.height < 1) return false;

                    const holder = document.createElement('div');
                    holder.id = 'pdfengine-float-capture';
                    // Absolute, not fixed: Playwright can scroll to an absolutely
                    // positioned element that is larger than the viewport, and a
                    // full-width float at 2x is larger than the viewport.
                    holder.style.cssText = 'position:absolute;left:0;top:0;background:#ffffff;'
                        + 'z-index:2147483647;overflow:hidden;'
                        + 'width:' + (rect.width * scale) + 'px;'
                        + 'height:' + (rect.height * scale) + 'px;';

                    const clone = el.cloneNode(true);
                    clone.removeAttribute('data-pdfengine-pagefloat');
                    clone.style.setProperty('display', 'block', 'important');
                    clone.style.width = rect.width + 'px';
                    clone.style.margin = '0';
                    clone.style.transform = 'scale(' + scale + ')';
                    clone.style.transformOrigin = 'top left';
                    holder.appendChild(clone);
                    document.body.appendChild(holder);
                    return true;
                }", new object[] { floated.Number, CaptureScale });

                if (!prepared) { failed++; continue; }

                var holderHandle = await page.QuerySelectorAsync("#pdfengine-float-capture");
                if (holderHandle == null) { failed++; continue; }

                var png = await holderHandle.ScreenshotAsync(new ElementHandleScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    Animations = ScreenshotAnimations.Disabled
                });
                floated.ImageBase64 = Convert.ToBase64String(png);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to capture page float {Number}; it will be left where it was authored.", floated.Number);
            }
            finally
            {
                try
                {
                    await page.EvaluateAsync(
                        "() => { const h = document.getElementById('pdfengine-float-capture'); if (h) h.remove(); }");
                }
                catch (Exception) { /* the next capture replaces it by id anyway */ }
            }
        }

        // Only elements that were actually captured are hidden. Anything that failed stays
        // exactly where the author put it, which is Chromium's own behaviour — a float
        // that is neither placed nor left behind would be content silently deleted.
        var captured = plan.PageFloats.Where(f => f.ImageBase64.Length > 0).Select(f => f.Number).ToArray();
        if (captured.Length > 0)
        {
            await page.EvaluateAsync(@"(numbers) => {
                for (const n of numbers) {
                    const el = document.querySelector('[data-pdfengine-pagefloat=""' + n + '""]');
                    if (el) el.style.setProperty('display', 'none', 'important');
                }
            }", captured);
        }

        plan.PageFloats.RemoveAll(f => f.ImageBase64.Length == 0);

        if (failed > 0)
        {
            warnings.Add(
                $"Page float warning: {failed} floated element(s) could not be captured and were left exactly where they were authored, mid-flow. The document is complete but those elements are not at a page edge.");
        }
    }

    /// <summary>
    /// Draws the captured page floats into the bands the reflow loop reserved for them.
    ///
    /// `top` floats stack downwards from the top of the page area; `bottom` floats stack
    /// upwards from above the footnote band, so a page carrying both keeps the footnotes
    /// lowest — which is where a reader expects them.
    /// </summary>
    private static byte[] StampPageFloats(
        byte[] pdfBytes, PaginationPlan plan, RenderingOptions options,
        ILogger? logger = null, List<string>? diagnosticWarnings = null)
    {
        var placed = plan.PageFloats.Where(f => f.Page > 0 && f.ImageBase64.Length > 0).ToList();
        if (placed.Count == 0) return pdfBytes;

        var floatBox = ResolveContentBoxPt(pdfBytes, options);
        var marginLeftPt = floatBox.Left;
        var marginRightPt = floatBox.Right;
        var topBasePt = plan.FloatBandBaseTopPt > 0 ? plan.FloatBandBaseTopPt : ResolveMarginPt(options.MarginTop);
        var bottomBasePt = plan.FootnoteBandBaseYPt > 0 ? plan.FootnoteBandBaseYPt : ResolveMarginPt(options.MarginBottom);

        using var input = new MemoryStream(pdfBytes);
        using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
            input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

        using var measure = PdfSharpCore.Drawing.XGraphics.CreateMeasureContext(
            new PdfSharpCore.Drawing.XSize(1000, 1000),
            PdfSharpCore.Drawing.XGraphicsUnit.Point,
            PdfSharpCore.Drawing.XPageDirection.Downwards);

        foreach (var group in placed.GroupBy(f => f.Page).OrderBy(g => g.Key))
        {
            if (group.Key < 1 || group.Key > document.PageCount) continue;

            var pdfPage = document.Pages[group.Key - 1];
            var contentWidth = Math.Max(72.0, pdfPage.Width.Point - marginLeftPt - marginRightPt);

            using var gfx = PdfSharpCore.Drawing.XGraphics.FromPdfPage(
                pdfPage, PdfSharpCore.Drawing.XGraphicsPdfPageOptions.Append);

            // Top floats run downwards from the top of the content area.
            var topCursor = topBasePt;
            foreach (var floated in group.Where(f => f.Edge == "top").OrderBy(f => f.Number))
            {
                DrawPageFloat(gfx, floated, marginLeftPt, topCursor, contentWidth, logger);
                topCursor += FitPageFloatHeightPt(floated, contentWidth) + PageFloatGapPt;
            }

            // Bottom floats stack upwards, starting above whatever footnote band this page
            // already has, so the two features do not draw over each other.
            var footnoteBand = plan.Footnotes.Any(f => f.Page == group.Key)
                ? ComputeFootnoteBandHeightPt(measure,
                    plan.Footnotes.Where(f => f.Page == group.Key).OrderBy(f => f.Number).ToList(),
                    plan.FootnoteArea, contentWidth)
                : 0;

            var bottomCursor = pdfPage.Height.Point - bottomBasePt - footnoteBand;
            foreach (var floated in group.Where(f => f.Edge != "top").OrderByDescending(f => f.Number))
            {
                var height = FitPageFloatHeightPt(floated, contentWidth);
                bottomCursor -= height;
                DrawPageFloat(gfx, floated, marginLeftPt, bottomCursor, contentWidth, logger);
                bottomCursor -= PageFloatGapPt;
            }
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    /// <summary>Vertical breathing room between a page float and the text beside it.</summary>
    private const double PageFloatGapPt = 6;

    /// <summary>
    /// The height the float actually occupies once it has been fitted to the content
    /// width. An element wider than the printable area is scaled down rather than clipped,
    /// so the reservation and the drawing agree on a single number.
    /// </summary>
    private static double FitPageFloatHeightPt(PageFloatAssignment floated, double contentWidthPt)
    {
        if (floated.WidthPt <= 0 || floated.HeightPt <= 0) return 0;
        var scale = Math.Min(1.0, contentWidthPt / floated.WidthPt);
        return floated.HeightPt * scale;
    }

    /// <summary>
    /// Attaches a clickable link over a drawn footnote run.
    ///
    /// Annotation rectangles are in PDF coordinates — origin at the BOTTOM-left of the page
    /// — while the band is laid out top-down, so the y has to be flipped.
    /// </summary>
    private static void AddFootnoteLink(
        PdfSharpCore.Pdf.PdfPage page, string href, double xPt, double yTopDown,
        double widthPt, double heightPt)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            var bottom = page.Height.Point - (yTopDown + heightPt);
            page.AddWebLink(
                new PdfSharpCore.Pdf.PdfRectangle(
                    new PdfSharpCore.Drawing.XPoint(xPt, bottom),
                    new PdfSharpCore.Drawing.XPoint(xPt + widthPt, bottom + heightPt)),
                uri.AbsoluteUri);
        }
        catch (Exception)
        {
            // A link that cannot be attached costs the reader a click, not the document.
        }
    }

    /// <summary>
    /// Lays the float's own words back over its picture as INVISIBLE text.
    ///
    /// A page float is drawn as an image and images carry no text, so without this the
    /// words inside a floated table or caption cannot be selected, searched, or read by a
    /// screen reader. Drawing them again with a fully transparent brush is the same
    /// technique a scanned document uses for its OCR layer: nothing changes visually, and
    /// everything becomes selectable. Verified: transparent text round-trips through
    /// extraction intact.
    /// </summary>
    private static void DrawPageFloatTextLayer(
        PdfSharpCore.Drawing.XGraphics gfx, PageFloatAssignment floated,
        double xPt, double yPt, double scale)
    {
        if (floated.TextRuns.Count == 0) return;

        var invisible = new PdfSharpCore.Drawing.XSolidBrush(
            PdfSharpCore.Drawing.XColor.FromArgb(0, 0, 0, 0));

        foreach (var run in floated.TextRuns)
        {
            if (string.IsNullOrWhiteSpace(run.Text)) continue;
            try
            {
                var size = Math.Clamp(run.FontSizePt * scale, 1, 200);
                var font = new PdfSharpCore.Drawing.XFont("Helvetica", size);
                gfx.DrawString(run.Text, font, invisible,
                    new PdfSharpCore.Drawing.XRect(
                        xPt + run.XPt * scale, yPt + run.YPt * scale,
                        Math.Max(1, run.WidthPt * scale), Math.Max(1, run.HeightPt * scale)),
                    PdfSharpCore.Drawing.XStringFormats.TopLeft);
            }
            catch (Exception)
            {
                // One unrenderable run must not cost the whole text layer.
            }
        }
    }

    private static void DrawPageFloat(
        PdfSharpCore.Drawing.XGraphics gfx, PageFloatAssignment floated,
        double xPt, double yPt, double contentWidthPt, ILogger? logger)
    {
        try
        {
            var bytes = Convert.FromBase64String(floated.ImageBase64);
            using var stream = new MemoryStream(bytes);
            using var image = PdfSharpCore.Drawing.XImage.FromStream(() => stream);

            var scale = Math.Min(1.0, contentWidthPt / Math.Max(1.0, floated.WidthPt));
            var width = floated.WidthPt * scale;
            var height = floated.HeightPt * scale;

            // Centred in the content width, which is how a page float that is narrower
            // than the text column is conventionally set.
            var x = xPt + Math.Max(0, (contentWidthPt - width) / 2);
            gfx.DrawImage(image, x, yPt, width, height);
            DrawPageFloatTextLayer(gfx, floated, x, yPt, scale);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to draw page float {Number}.", floated.Number);
        }
    }

    private static double ResolveMarginPt(RenderingOptions options) => ResolveMarginPt(options.MarginTop);

    private static double ResolveMarginPt(string? raw)
    {
        // Falls back to a sane default rather than 0: a margin box drawn at y=0 would sit
        // on the very edge of the sheet and be cropped by most printers.
        var m = Regex.Match(raw ?? string.Empty, @"([\d.]+)\s*(mm|cm|in|pt|px)?", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
            return 36;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "mm" => n * 72.0 / 25.4,
            "cm" => n * 72.0 / 2.54,
            "in" => n * 72.0,
            "px" => n * 0.75,
            _ => n
        };
    }

    private static PdfSharpCore.Drawing.XColor ParseColor(string? color)
    {
        var c = (color ?? "#000000").Trim();
        if (c.StartsWith("#", StringComparison.Ordinal))
        {
            c = c[1..];
            if (c.Length == 3) c = string.Concat(c.Select(ch => new string(ch, 2)));
            if (c.Length == 6
                && int.TryParse(c, System.Globalization.NumberStyles.HexNumber,
                       System.Globalization.CultureInfo.InvariantCulture, out var v))
                return PdfSharpCore.Drawing.XColor.FromArgb(
                    (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }
        return PdfSharpCore.Drawing.XColors.Black;
    }

    /// <summary>
    /// True when the HTML declares a <c>size</c> descriptor inside an <c>@page</c> block.
    /// Deliberately narrow: a bare <c>@page { margin: ... }</c> must NOT count, because
    /// margin-only rules are extremely common and dropping Format for them would silently
    /// change every such document's paper size to Chromium's default.
    /// </summary>
    private static readonly Regex CssPageSizePattern = new(
        @"@page[^{]*\{[^}]*(?<!-)\bsize\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool DeclaresCssPageSize(string? html) =>
        !string.IsNullOrEmpty(html) && CssPageSizePattern.IsMatch(html);

    // --- Gate J: determinism controls -------------------------------------------

    /// <summary>
    /// Places a script as early in the document as possible so it runs before any author
    /// script. This runs AFTER sanitization deliberately — it is engine-generated code
    /// responding to an explicit caller option, not untrusted input, and routing it
    /// through the sanitizer would strip it whenever `allowScripts` is false. That would
    /// silently disable determinism for exactly the safe, script-free documents most
    /// likely to want reproducible output.
    /// </summary>
    private static string InjectHeadScript(string html, string script)
    {
        var tag = $"<script>{script}</script>";

        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (head >= 0)
        {
            var close = html.IndexOf('>', head);
            if (close > 0) return html.Insert(close + 1, tag);
        }

        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (body >= 0)
        {
            var close = html.IndexOf('>', body);
            if (close > 0) return html.Insert(close + 1, tag);
        }

        // Fragment with no head or body: the browser wraps it, and a leading script still
        // executes before the rest of the fragment.
        return tag + html;
    }

    /// <summary>
    /// Fails the render when the caller pinned an engine version that is not the one
    /// running. Failing is the feature: silently rendering with a different Chromium is
    /// exactly the drift the pin exists to detect.
    /// </summary>
    private static void EnsureEngineVersionMatches(string? pinned, IBrowser browser)
    {
        PdfEngine.Application.Common.EngineVersion.SetChromiumVersion(browser.Version);
        if (string.IsNullOrWhiteSpace(pinned)) return;

        var current = PdfEngine.Application.Common.EngineVersion.Current;
        var wanted = pinned.Trim();

        // A profile-only pin ("2026.08") is honoured as a prefix so callers can pin
        // layout behaviour without re-pinning on every Chromium patch release. Pinning
        // the full string is the stricter option and remains available.
        if (current.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return;
        if (!wanted.Contains('+') &&
            PdfEngine.Application.Common.EngineVersion.Profile
                .Equals(wanted, StringComparison.OrdinalIgnoreCase)) return;

        throw new InvalidOperationException(
            $"Engine version mismatch: caller pinned '{wanted}' but this engine is "
            + $"'{current}'. Output is not guaranteed to match the pinned version, so the "
            + "render was refused rather than silently produced by a different engine.");
    }

    /// <summary>
    /// Builds the init script that freezes the page clock and seeds randomness, or null
    /// when the caller asked for neither. Returning null matters: injecting a no-op script
    /// on every render would add cost to the overwhelmingly common non-deterministic path.
    /// </summary>
    private static string? BuildDeterminismInitScript(RenderingOptions options)
    {
        if (options.FixedDateUtc == null && options.RandomSeed == null) return null;

        var sb = new StringBuilder("(() => {\n");

        if (options.FixedDateUtc is { } fixedDate)
        {
            var epochMs = new DateTimeOffset(
                DateTime.SpecifyKind(fixedDate, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

            // Date is subclassed rather than monkey-patched so `instanceof Date`,
            // date arithmetic and every getter keep working — libraries routinely rely
            // on all three, and a naive `Date.now = ...` override breaks them.
            sb.Append(@"
  const FIXED = ").Append(epochMs).Append(@";
  const RealDate = Date;
  class FrozenDate extends RealDate {
    constructor(...args) { super(...(args.length ? args : [FIXED])); }
    static now() { return FIXED; }
  }
  FrozenDate.parse = RealDate.parse;
  FrozenDate.UTC = RealDate.UTC;
  Date = FrozenDate;
  if (window.performance) { performance.now = () => 0; }
");
        }

        if (options.RandomSeed is { } seed)
        {
            // mulberry32: small, fast, well-distributed, and identical across runs for a
            // given seed — the only properties that matter here.
            sb.Append(@"
  let s = ").Append(unchecked((uint)seed)).Append(@" >>> 0;
  Math.random = () => {
    s = (s + 0x6D2B79F5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
");
        }

        return sb.Append("})();").ToString();
    }

    // --- RB-2: logical-order text recovery for RTL runs -------------------------
    private static readonly Regex ReversedRunPattern =
        new(@"/ReversedChars\s+BMC(.*?)\bEMC\b", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex FontSelectPattern =
        new(@"/([A-Za-z0-9]+)\s+[\d.]+\s+Tf", RegexOptions.Compiled);
    private static readonly Regex ShowGlyphPattern =
        new(@"<([0-9A-Fa-f]+)>\s*(?:Tj|TJ)|\[(.*?)\]\s*TJ", RegexOptions.Compiled);
    private static readonly Regex BfCharPairPattern =
        new(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>", RegexOptions.Compiled);
    private static readonly Regex HexStringPattern =
        new(@"<([0-9A-Fa-f]+)>", RegexOptions.Compiled);

    /// <summary>
    /// Attaches <c>/ActualText</c> to Chromium's <c>/ReversedChars</c> runs so RTL text
    /// survives copy/paste and search (RB-2).
    ///
    /// Chromium stores an RTL run in VISUAL order and marks it <c>/ReversedChars BMC</c>,
    /// meaning "reverse these to recover logical order". Extractors do reverse it — but at
    /// CHARACTER level, whereas a single glyph may map to MULTIPLE characters. The Arabic
    /// lam-alef ligature is one glyph whose ToUnicode value is two characters
    /// (U+0644 U+0627); reversing characters splits it and emits them backwards:
    ///
    ///   glyphs  &lt;0154&gt;&lt;0164&gt;&lt;00DA&gt;&lt;00F0&gt;  =  lam, waw, [lam+alef], alef
    ///   glyph-level reverse  -> alef, [lam+alef], waw, lam   CORRECT
    ///   char-level  reverse  -> alef, alef, lam, waw, lam    WHAT EXTRACTORS PRODUCE
    ///
    /// The /ToUnicode map is already correct, so rewriting it cannot help — an earlier
    /// attempt to do exactly that was measured as a no-op and reverted. The fix is to state
    /// the logical text explicitly: reverse the GLYPH list (keeping each glyph's own
    /// characters in order) and emit it as /ActualText, which takes precedence over glyph
    /// decoding for every conformant extractor.
    ///
    /// The original <c>/ReversedChars BMC</c> is NESTED INSIDE rather than replaced, so any
    /// consumer that ignores /ActualText still sees exactly the bytes it saw before — this
    /// can improve extraction but cannot regress it. Runs are only touched when the decoded
    /// text actually contains RTL characters, so LTR documents are untouched and the
    /// document is re-saved only if at least one span was written.
    /// </summary>
    internal static byte[] ApplyActualTextToReversedRuns(
        byte[] pdfBytes, ILogger? logger = null, List<string>? diagnosticWarnings = null)
    {
        try
        {
            using var input = new MemoryStream(pdfBytes);
            using var document = PdfSharpCore.Pdf.IO.PdfReader.Open(
                input, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Modify);

            var spans = 0;
            foreach (PdfSharpCore.Pdf.PdfPage page in document.Pages)
            {
                var cmaps = BuildResourceCMaps(page);
                if (cmaps.Count == 0) continue;

                var contents = page.Contents;
                for (var i = 0; i < contents.Elements.Count; i++)
                {
                    var streamDict = contents.Elements.GetDictionary(i);
                    if (streamDict?.Stream == null) continue;

                    var original = Encoding.Latin1.GetString(streamDict.Stream.UnfilteredValue);
                    var written = 0;

                    var updated = ReversedRunPattern.Replace(original, match =>
                    {
                        var logical = DecodeRunToLogicalOrder(match.Groups[1].Value, cmaps);
                        if (string.IsNullOrWhiteSpace(logical) || !ContainsRtl(logical))
                            return match.Value;

                        written++;
                        // REPLACE the /ReversedChars marker rather than wrapping it. Nesting
                        // was tried first and measured wrong: the extractor honours
                        // /ActualText AND THEN still applies the /ReversedChars reversal on
                        // top, so correct logical text came back out fully reversed
                        // ("الاتجاه" -> "هاجتالا"). Only one of the two may describe the run.
                        return $"/Span <</ActualText {ToPdfUtf16BeHexString(logical)}>> BDC"
                             + match.Groups[1].Value + "EMC";
                    });

                    if (written == 0) continue;

                    streamDict.Stream.Value = Encoding.Latin1.GetBytes(updated);
                    // The stream now holds decoded bytes, so any prior filter no longer
                    // describes them and must be dropped along with a corrected length.
                    streamDict.Elements.Remove("/Filter");
                    streamDict.Elements.SetInteger("/Length", streamDict.Stream.Value.Length);
                    spans += written;
                }
            }

            if (spans == 0) return pdfBytes;

            using var output = new MemoryStream();
            document.Save(output);
            diagnosticWarnings?.Add(
                $"Text-layer notice: attached /ActualText to {spans} right-to-left run(s) so "
                + "extracted text is in logical order (fixes copy/paste and search for Arabic, "
                + "Persian, Urdu and Hebrew content).");
            return output.ToArray();
        }
        catch (Exception ex)
        {
            // A text-layer improvement must never fail an otherwise-good render.
            logger?.LogWarning(ex, "/ActualText annotation failed; returning the PDF unchanged.");
            return pdfBytes;
        }
    }

    /// <summary>Maps each page-resource font name (e.g. "F4") to its glyph-code -> string CMap.</summary>
    private static Dictionary<string, Dictionary<string, string>> BuildResourceCMaps(
        PdfSharpCore.Pdf.PdfPage page)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var fonts = page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/Font");
        if (fonts == null) return result;

        foreach (var name in fonts.Elements.Keys)
        {
            var font = fonts.Elements.GetDictionary(name);
            var tu = font?.Elements["/ToUnicode"];
            var streamDict = tu is PdfSharpCore.Pdf.Advanced.PdfReference reference
                ? reference.Value as PdfSharpCore.Pdf.PdfDictionary
                : tu as PdfSharpCore.Pdf.PdfDictionary;
            if (streamDict?.Stream == null) continue;

            var cmap = ParseToUnicodeCMap(
                Encoding.Latin1.GetString(streamDict.Stream.UnfilteredValue));
            if (cmap.Count > 0) result[name.TrimStart('/')] = cmap;
        }
        return result;
    }

    /// <summary>
    /// Parses the bfchar entries of a /ToUnicode CMap into glyph-code -> string. bfrange is
    /// deliberately NOT parsed: Chromium's subset fonts emit bfchar only (confirmed against
    /// real output), and a half-understood range parse would silently produce wrong text —
    /// worse than leaving the run alone, which is what an unmapped glyph causes here.
    /// </summary>
    private static Dictionary<string, string> ParseToUnicodeCMap(string cmapText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cursor = 0;
        while (true)
        {
            var start = cmapText.IndexOf("beginbfchar", cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var end = cmapText.IndexOf("endbfchar", start, StringComparison.Ordinal);
            if (end < 0) break;

            foreach (Match m in BfCharPairPattern.Matches(cmapText[start..end]))
            {
                var value = HexToUtf16(m.Groups[2].Value);
                if (value != null) map[m.Groups[1].Value] = value;
            }
            cursor = end + 1;
        }
        return map;
    }

    /// <summary>Decodes a visual-order run and returns it in logical order.</summary>
    private static string DecodeRunToLogicalOrder(
        string runBody, Dictionary<string, Dictionary<string, string>> cmaps)
    {
        Dictionary<string, string>? active = null;
        var glyphs = new List<string>();

        // Font selection and glyph-showing operators are interleaved, so walk them in
        // source order rather than collecting each kind separately.
        var tokens = Regex.Matches(runBody,
            @"/([A-Za-z0-9]+)\s+[\d.]+\s+Tf|<([0-9A-Fa-f]+)>\s*Tj|\[(.*?)\]\s*TJ",
            RegexOptions.Singleline);

        foreach (Match t in tokens)
        {
            if (t.Groups[1].Success)
            {
                cmaps.TryGetValue(t.Groups[1].Value, out active);
            }
            else if (t.Groups[2].Success)
            {
                if (!TryAppendGlyphs(t.Groups[2].Value, active, glyphs)) return string.Empty;
            }
            else if (t.Groups[3].Success)
            {
                foreach (Match h in HexStringPattern.Matches(t.Groups[3].Value))
                    if (!TryAppendGlyphs(h.Groups[1].Value, active, glyphs)) return string.Empty;
            }
        }

        if (glyphs.Count == 0) return string.Empty;

        // Intervene ONLY where the defect can exist: a glyph whose ToUnicode value is more
        // than one character. When every glyph maps 1:1, the extractor's character-level
        // reversal is already correct, and adding /ActualText measurably made things worse
        // — it moved sentence-final punctuation outside the span and regressed the
        // `arabic-simple` fixture from PASS to PARTIAL. Narrow scope keeps this a strict
        // improvement instead of a trade.
        if (!glyphs.Any(g => g.Length > 1)) return string.Empty;

        glyphs.Reverse();               // reverse GLYPHS, never the characters inside one
        return string.Concat(glyphs);
    }

    /// <summary>
    /// Splits a hex string into 2-byte glyph codes and appends each glyph's mapped text.
    /// Returns false if ANY glyph is unmapped — a partially decoded run would yield text
    /// that looks plausible but is missing characters, so the whole run is abandoned and
    /// left exactly as Chromium wrote it.
    /// </summary>
    private static bool TryAppendGlyphs(
        string hex, Dictionary<string, string>? cmap, List<string> sink)
    {
        if (cmap == null || hex.Length == 0 || hex.Length % 4 != 0) return false;
        for (var i = 0; i < hex.Length; i += 4)
        {
            if (!cmap.TryGetValue(hex.Substring(i, 4), out var text)) return false;
            sink.Add(text);
        }
        return true;
    }

    private static string? HexToUtf16(string hex)
    {
        if (hex.Length == 0 || hex.Length % 4 != 0) return null;
        var sb = new StringBuilder(hex.Length / 4);
        for (var i = 0; i < hex.Length; i += 4)
        {
            if (!ushort.TryParse(hex.AsSpan(i, 4), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var unit))
                return null;
            if (unit == 0) return null;     // .notdef carries no recoverable text
            sb.Append((char)unit);
        }
        return sb.ToString();
    }

    private static bool ContainsRtl(string value) => value.Any(c =>
        (c >= '֐' && c <= '׿') ||    // Hebrew
        (c >= '؀' && c <= 'ۿ') ||    // Arabic
        (c >= '܀' && c <= 'ݏ') ||    // Syriac
        (c >= 'ݐ' && c <= 'ݿ') ||    // Arabic Supplement
        (c >= 'ࢠ' && c <= 'ࣿ') ||    // Arabic Extended-A
        (c >= 'יִ' && c <= '﷿') ||    // Hebrew/Arabic Presentation Forms-A
        (c >= 'ﹰ' && c <= '﻿'));     // Arabic Presentation Forms-B

    /// <summary>PDF hex string, UTF-16BE with the U+FEFF marker the spec requires.</summary>
    private static string ToPdfUtf16BeHexString(string value)
    {
        var sb = new StringBuilder("<FEFF", value.Length * 4 + 6);
        foreach (var c in value) sb.Append(((int)c).ToString("X4"));
        return sb.Append('>').ToString();
    }

    private static WaitUntilState ResolveWaitUntil(string? waitUntil) => waitUntil?.Trim().ToLowerInvariant() switch
    {
        "networkidle" => WaitUntilState.NetworkIdle,
        "domcontentloaded" => WaitUntilState.DOMContentLoaded,
        "commit" => WaitUntilState.Commit,
        _ => WaitUntilState.Load
    };

    private static int GetPdfPageCount(byte[] pdfBytes)
    {
        try
        {
            var content = Encoding.UTF8.GetString(pdfBytes);
            var countIdx = content.IndexOf("/Type /Pages", StringComparison.Ordinal);
            if (countIdx != -1)
            {
                var countSearch = content.Substring(Math.Max(0, countIdx - 100), Math.Min(content.Length - countIdx, 300));
                var match = Regex.Match(countSearch, @"/Count\s+(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pageCount))
                {
                    return pageCount;
                }
            }

            // Fallback scan
            var count = 0;
            var idx = 0;
            while ((idx = content.IndexOf("/Type /Page", idx, StringComparison.Ordinal)) != -1)
            {
                if (idx + 11 < content.Length && content[idx + 11] != 's')
                {
                    count++;
                }
                idx += 11;
            }
            return count > 0 ? count : 1;
        }
        catch
        {
            return 1; // Return safe default
        }
    }



    private static List<string> RunHtmlCssPreflightDoctor(string html)
    {
        var warnings = new List<string>();
        
        // 1. Template Expression Checkers
        var templatePatterns = new[]
        {
            (@"\$\{[^}]+\}", "${variable}"),
            (@"\{\{[^}]+\}\}", "{{variable}}"),
            (@"<%=[^%]+%>", "<%= variable %>"),
            (@"<%[^%]+%>", "<% variable %>"),
            (@"\{%[^%]+%\}", "{% variable %}")
        };

        bool foundTemplate = false;
        foreach (var pattern in templatePatterns)
        {
            if (Regex.IsMatch(html, pattern.Item1))
            {
                foundTemplate = true;
                break;
            }
        }

        if (foundTemplate)
        {
            warnings.Add("Template expressions detected (e.g. {{variable}} or ${variable}). PDFEngine does not execute template syntax on the server. Your HTML should be pre-compiled, otherwise these placeholders will render as literal text in the final PDF.");
        }

        // 2. Unsupported / Unstable CSS Checkers
        if (html.Contains("backdrop-filter", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: 'backdrop-filter' detected. Backdrop filters are not supported in print PDF layouts. Use standard semi-transparent background colors instead.");
        }
        if (html.Contains("mix-blend-mode", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: 'mix-blend-mode' detected. Blend modes have unstable support in print rendering. Use pre-rendered images or flat opacity layers.");
        }
        if (html.Contains("filter:", StringComparison.OrdinalIgnoreCase) || html.Contains("filter :", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: 'filter' detected. Graphical CSS filters can cause severe layout rendering lag or print layout inconsistencies. Use flat or pre-filtered assets.");
        }
        if (html.Contains("mask-image", StringComparison.OrdinalIgnoreCase) || 
            html.Contains("-webkit-mask", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(html, @"\bmask\s*:", RegexOptions.IgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: CSS Complex Masking detected. Mask properties are unstable in print engines. Use transparent PNG assets instead.");
        }
        if (html.Contains("container-type", StringComparison.OrdinalIgnoreCase) || html.Contains("container-name", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: CSS Container Queries detected. Print engines do not always evaluate container queries correctly. Use standard media queries or flexbox layouts.");
        }
        if (html.Contains("position: fixed", StringComparison.OrdinalIgnoreCase) || html.Contains("position:fixed", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: 'position: fixed' detected. Fixed position elements can overlap content or duplicate unpredictably across pages. Use margin-top/bottom or header/footer templates.");
        }
        
        if (!html.Contains("@page", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: Missing '@page' margin rule. Add '@page { size: A4; margin: 12mm; }' to ensure layouts do not crop at physical boundaries.");
        }

        if (html.Contains("<table", StringComparison.OrdinalIgnoreCase) && !html.Contains("table-header-group", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Actionable CSS Suggestion: Table detected without 'table-header-group'. To repeat table headers across pages, add: 'thead { display: table-header-group; }'.");
        }

        return warnings;
    }

    /// <summary>
    /// Real perceptual pixel diff via SkiaSharp — decodes both PNGs and compares actual
    /// pixel color values (with a small per-channel tolerance for anti-aliasing noise),
    /// replacing what used to be a byte-for-byte comparison of the compressed PNG file
    /// bytes. That approach was meaningless for a compressed format: a single-byte
    /// encoding shift anywhere in the stream (which carries no visual meaning at all)
    /// would cascade and register as near-100% drift.
    /// </summary>
    internal static double ComputeVisualDrift(byte[] currentPng, byte[] referencePng)
    {
        if (currentPng == null || referencePng == null) return 100.0;

        using var current = SKBitmap.Decode(currentPng);
        using var reference = SKBitmap.Decode(referencePng);
        if (current == null || reference == null) return 100.0;

        // Resample the reference onto the current image's dimensions so every pixel has
        // a counterpart to compare — a page-size or viewport change shouldn't make the
        // comparison degrade to a meaningless length ratio.
        SKBitmap referenceForCompare = reference;
        var disposeReference = false;
        if (reference.Width != current.Width || reference.Height != current.Height)
        {
            var resized = reference.Resize(new SKImageInfo(current.Width, current.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            if (resized == null) return 100.0;
            referenceForCompare = resized;
            disposeReference = true;
        }

        try
        {
            var totalPixels = current.Width * current.Height;
            if (totalPixels == 0) return 100.0;

            var currentPixels = current.Pixels;
            var referencePixels = referenceForCompare.Pixels;
            const int channelTolerance = 12;

            long differingPixels = 0;
            for (int i = 0; i < totalPixels; i++)
            {
                var a = currentPixels[i];
                var b = referencePixels[i];

                if (Math.Abs(a.Red - b.Red) > channelTolerance ||
                    Math.Abs(a.Green - b.Green) > channelTolerance ||
                    Math.Abs(a.Blue - b.Blue) > channelTolerance ||
                    Math.Abs(a.Alpha - b.Alpha) > channelTolerance)
                {
                    differingPixels++;
                }
            }

            return (double)differingPixels / totalPixels * 100.0;
        }
        finally
        {
            if (disposeReference) referenceForCompare.Dispose();
        }
    }



    private static string InjectMetadataTags(string html, PdfEngine.Application.DTOs.RenderingOptions options)
    {
        var tags = new StringBuilder();
        if (!string.IsNullOrEmpty(options.Title))
        {
            tags.AppendLine($"<title>{WebUtility.HtmlEncode(options.Title)}</title>");
            tags.AppendLine($"<meta name=\"title\" content=\"{WebUtility.HtmlEncode(options.Title)}\" />");
        }
        if (!string.IsNullOrEmpty(options.Author))
        {
            tags.AppendLine($"<meta name=\"author\" content=\"{WebUtility.HtmlEncode(options.Author)}\" />");
        }
        if (!string.IsNullOrEmpty(options.Subject))
        {
            tags.AppendLine($"<meta name=\"subject\" content=\"{WebUtility.HtmlEncode(options.Subject)}\" />");
        }
        if (!string.IsNullOrEmpty(options.Keywords))
        {
            tags.AppendLine($"<meta name=\"keywords\" content=\"{WebUtility.HtmlEncode(options.Keywords)}\" />");
        }

        if (tags.Length == 0) return html;

        var headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx != -1)
        {
            return html.Insert(headIdx + 6, "\n" + tags.ToString());
        }
        
        var htmlIdx = html.IndexOf("<html>", StringComparison.OrdinalIgnoreCase);
        if (htmlIdx != -1)
        {
            return html.Insert(htmlIdx + 6, "\n<head>\n" + tags.ToString() + "</head>\n");
        }

        return "<head>\n" + tags.ToString() + "</head>\n" + html;
    }
}