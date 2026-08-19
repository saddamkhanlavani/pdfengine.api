using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;
using SkiaSharp;

namespace PdfEngine.Infrastructure.Services;

public class AssetOptimizerStage : IAssetOptimizerStage
{
    private readonly ILogger<AssetOptimizerStage> _logger;

    private static readonly Regex InlineImagePattern = new(
        @"src=([""'])data:image/(?<mime>[a-zA-Z0-9.+-]+);base64,(?<payload>[^""']+)\1",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AssetOptimizerStage(ILogger<AssetOptimizerStage> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task ExecuteAsync(RenderingContext context, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var html = context.Html;
        if (string.IsNullOrWhiteSpace(html))
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var optimize = context.Options?.OptimizeImages ?? false;
        var quality = Math.Clamp(context.Options?.ImageQuality ?? 82, 1, 100);

        try
        {
            long totalInlineBase64Bytes = 0;
            long totalOptimizedBytes = 0;
            int optimizedCount = 0;
            int skippedCount = 0;

            html = InlineImagePattern.Replace(html, match =>
            {
                var quoteChar = match.Groups[1].Value;
                var mime = match.Groups["mime"].Value;
                var payload = match.Groups["payload"].Value;
                var originalBytes = (payload.Length * 3L) / 4;
                totalInlineBase64Bytes += originalBytes;

                if (originalBytes > 3 * 1024 * 1024)
                {
                    context.Diagnostics?.Warnings.Add($"Asset Warning: Large inline base64 image detected ({(originalBytes / 1024.0 / 1024.0).ToString("0.0")} MB). External URLs or WebP image formats are recommended for optimal compilation speed.");
                }

                if (!optimize)
                {
                    return match.Value;
                }

                // Already-encoded WebP at a reasonable size isn't worth decoding and
                // re-encoding — that's pure CPU cost for no size benefit.
                if (mime.Equals("svg+xml", StringComparison.OrdinalIgnoreCase))
                {
                    skippedCount++;
                    totalOptimizedBytes += originalBytes;
                    return match.Value;
                }

                try
                {
                    var raw = Convert.FromBase64String(payload);
                    using var bitmap = SKBitmap.Decode(raw);
                    if (bitmap == null)
                    {
                        skippedCount++;
                        totalOptimizedBytes += originalBytes;
                        return match.Value;
                    }

                    using var image = SKImage.FromBitmap(bitmap);
                    using var encoded = image.Encode(SKEncodedImageFormat.Webp, quality);
                    var optimizedBytes = encoded.ToArray();

                    // Only replace when it's actually a net win — a tiny or
                    // already-optimal source image can come back larger post-WebP
                    // header overhead, and there's no reason to swap formats for a
                    // loss.
                    if (optimizedBytes.Length >= raw.Length)
                    {
                        skippedCount++;
                        totalOptimizedBytes += originalBytes;
                        return match.Value;
                    }

                    optimizedCount++;
                    totalOptimizedBytes += optimizedBytes.Length;
                    var newPayload = Convert.ToBase64String(optimizedBytes);
                    return $"src={quoteChar}data:image/webp;base64,{newPayload}{quoteChar}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to re-encode an inline image during asset optimization; leaving it as-is.");
                    skippedCount++;
                    totalOptimizedBytes += originalBytes;
                    return match.Value;
                }
            });

            if (optimize && optimizedCount > 0)
            {
                var savedBytes = totalInlineBase64Bytes - totalOptimizedBytes;
                var savedPct = totalInlineBase64Bytes > 0 ? (savedBytes / (double)totalInlineBase64Bytes * 100.0) : 0;
                context.Diagnostics?.Warnings.Add(
                    $"Asset Notice: Optimized {optimizedCount} inline image(s) to WebP at quality {quality} " +
                    $"({(totalInlineBase64Bytes / 1024.0).ToString("0")} KB -> {(totalOptimizedBytes / 1024.0).ToString("0")} KB, {savedPct:0}% smaller)" +
                    (skippedCount > 0 ? $"; {skippedCount} image(s) left unchanged (already optimal or unsupported format)." : "."));
            }

            if (totalInlineBase64Bytes > 10 * 1024 * 1024)
            {
                context.Diagnostics?.Warnings.Add($"Asset Notice: Total inline image payload exceeds 10 MB ({(totalInlineBase64Bytes / 1024.0 / 1024.0).ToString("0.0")} MB). Large inline assets may impact browser rendering performance.");
            }

            context.Html = html;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Asset Optimizer Stage encountered preflight analysis issue.");
        }

        return Task.CompletedTask;
    }
}
