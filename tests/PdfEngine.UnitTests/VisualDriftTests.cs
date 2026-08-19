using PdfEngine.Infrastructure.Services;
using SkiaSharp;
using Xunit;

namespace PdfEngine.UnitTests;

public class VisualDriftTests
{
    private static byte[] SolidColorPng(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void ComputeVisualDrift_IdenticalImages_ReportsNearZeroDrift()
    {
        var a = SolidColorPng(50, 50, SKColors.White);
        var b = SolidColorPng(50, 50, SKColors.White);

        var drift = PlaywrightPdfService.ComputeVisualDrift(a, b);

        Assert.Equal(0.0, drift, precision: 1);
    }

    [Fact]
    public void ComputeVisualDrift_CompletelyDifferentImages_ReportsFullDrift()
    {
        var white = SolidColorPng(50, 50, SKColors.White);
        var black = SolidColorPng(50, 50, SKColors.Black);

        var drift = PlaywrightPdfService.ComputeVisualDrift(white, black);

        Assert.Equal(100.0, drift, precision: 1);
    }

    [Fact]
    public void ComputeVisualDrift_MinorAntiAliasingNoise_IsToleratedAsNearZero()
    {
        // A color one shade off white should fall within the per-channel tolerance band
        // — this is exactly the class of false-positive the old byte-for-byte PNG
        // comparison could never distinguish from a real visual regression.
        var white = SolidColorPng(50, 50, SKColors.White);
        var almostWhite = SolidColorPng(50, 50, new SKColor(250, 250, 250));

        var drift = PlaywrightPdfService.ComputeVisualDrift(white, almostWhite);

        Assert.Equal(0.0, drift, precision: 1);
    }

    [Fact]
    public void ComputeVisualDrift_DifferentDimensions_StillProducesAMeaningfulComparison()
    {
        var a = SolidColorPng(100, 100, SKColors.White);
        var b = SolidColorPng(40, 40, SKColors.White);

        var drift = PlaywrightPdfService.ComputeVisualDrift(a, b);

        Assert.Equal(0.0, drift, precision: 1);
    }
}
