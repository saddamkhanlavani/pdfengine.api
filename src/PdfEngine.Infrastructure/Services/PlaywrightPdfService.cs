using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PdfEngine.Application.Common;
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

    // PHASE 10: Prometheus Metrics
    private static readonly Counter PdfGeneratedCounter = Metrics.CreateCounter(
        "pdf_generation_total", "Total number of PDFs generated", 
        new CounterConfiguration { LabelNames = new[] { "tenant", "status" } });

    private static readonly Histogram PdfDurationHistogram = Metrics.CreateHistogram(
        "pdf_generation_duration_seconds", "Time taken to generate PDF",
        new HistogramConfiguration { LabelNames = new[] { "tenant" } });

    public PlaywrightPdfService(
        IBrowserManager browserManager, 
        ILogger<PlaywrightPdfService> logger)
    {
        _browserManager = browserManager;
        _logger = logger;
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
        
        try
        {
            var browser = await _browserManager.GetBrowserAsync(cancellationToken);
            
            // Allocate custom timeout context
            var timeoutMs = planConfig.MaxRenderDurationSeconds * 1000;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ExtraHTTPHeaders = new[]
                {
                    new KeyValuePair<string, string>("X-PdfEngine-Render", "1.0.0")
                }
            });
            page.SetDefaultNavigationTimeout(timeoutMs);
            page.SetDefaultTimeout(timeoutMs);

            try
            {
                // Enforce asset download sizes
                long totalDownloadedBytes = 0;
                long maxAssetBytes = (long)(planConfig.MaxAssetDownloadMb * 1024 * 1024);

                page.Response += (sender, response) =>
                {
                    if (response.Headers.TryGetValue("content-length", out var lenStr) && 
                        long.TryParse(lenStr, out var length))
                    {
                        var currentTotal = Interlocked.Add(ref totalDownloadedBytes, length);
                        if (currentTotal > maxAssetBytes)
                        {
                            _logger.LogWarning("Job exceeded asset download limit of {Max}MB", planConfig.MaxAssetDownloadMb);
                            // Throws to trigger failure block safely
                            throw new Exception($"Asset download size exceeded maximum limit of {planConfig.MaxAssetDownloadMb}MB on current plan.");
                        }
                    }
                };

                // Route filter for SSRF defense
                await page.RouteAsync("**/*", async route =>
                {
                    try
                    {
                        var url = new Uri(route.Request.Url);
                        if (url.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                            url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                        {
                            var host = url.DnsSafeHost;
                            var ips = await Dns.GetHostAddressesAsync(host);
                            foreach (var ip in ips)
                            {
                                if (!IsIpSafe(ip))
                                {
                                    _logger.LogWarning("SSRF Defense blocked request to {Url} (resolved: {Ip})", route.Request.Url, ip);
                                    await route.AbortAsync();
                                    return;
                                }
                            }
                        }
                        await route.ContinueAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SSRF validation failed for {Url}. Request aborted.", route.Request.Url);
                        await route.AbortAsync();
                    }
                });

                await page.SetContentAsync(command.HtmlContent, new PageSetContentOptions
                {
                    Timeout = timeoutMs,
                    WaitUntil = WaitUntilState.Load
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

                var pdfOptions = new PagePdfOptions
                {
                    Format = command.Options.PageSize,
                    PrintBackground = command.Options.PrintBackground
                };

                var pdfBytes = await page.PdfAsync(pdfOptions);
                stopwatch.Stop();

                // Enforce Max Pages Limit
                var pageCount = GetPdfPageCount(pdfBytes);
                if (pageCount > planConfig.MaxPages)
                {
                    throw new Exception($"PDF page count ({pageCount}) exceeded maximum allowed pages ({planConfig.MaxPages}) on current plan.");
                }

                // Record Prometheus metrics
                PdfGeneratedCounter.WithLabels(tenantName, "success").Inc();
                PdfDurationHistogram.WithLabels(tenantName).Observe(stopwatch.Elapsed.TotalSeconds);
                
                return Result<byte[]>.Success(pdfBytes);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            PdfGeneratedCounter.WithLabels(tenantName, "failure").Inc();
            _logger.LogError(ex, "PDF generation failed for tenant {Tenant}", tenantName);
            return Result<byte[]>.Fail(new Error("PDF_GENERATION_FAILED", ex.Message));
        }
    }

    private static bool IsIpSafe(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        // Check IPv6 loopback, site-local, link-local
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal)
                return false;
            
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }
            else
            {
                return true;
            }
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            
            // Loopback check
            if (bytes[0] == 127) return false;
            
            // Private ranges: 10.0.0.0/8
            if (bytes[0] == 10) return false;
            
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;
            
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return false;
            
            // Link-local: 169.254.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254) return false;
        }

        return true;
    }

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
}