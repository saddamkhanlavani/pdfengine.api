using PdfEngine.Application.DTOs;
using PdfEngine.Infrastructure.Services;
using Xunit;

namespace PdfEngine.UnitTests;

public class PaginationPlannerTests
{
    [Fact]
    public void ComputePrintableHeightPx_A4Portrait_MatchesKnownDimensions()
    {
        var options = new RenderingOptions { PageSize = "A4", MarginTop = "0px", MarginBottom = "0px" };

        var height = PaginationPlanner.ComputePrintableHeightPx(options);

        Assert.Equal(1122.5, height, precision: 1);
    }

    [Fact]
    public void ComputePrintableHeightPx_DifferentPageSizes_ProduceDifferentHeights()
    {
        var a4 = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "A4" });
        var letter = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "Letter" });
        var legal = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "Legal" });

        // This is the core regression this fix targets: the old planner used a single
        // hardcoded 900px constant regardless of page size, so every size produced an
        // identical value. Real page geometry must differ per size.
        Assert.NotEqual(a4, letter);
        Assert.NotEqual(letter, legal);
        Assert.True(legal > letter, "Legal is taller than Letter and must yield a larger printable height.");
    }

    [Fact]
    public void ComputePrintableHeightPx_SubtractsMarginsInMillimeters()
    {
        var noMargin = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "A4", MarginTop = "0px", MarginBottom = "0px" });
        var withMargin = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "A4", MarginTop = "10mm", MarginBottom = "10mm" });

        // 20mm total margin ≈ 75.6px at 96dpi
        Assert.InRange(noMargin - withMargin, 74, 77);
    }

    [Fact]
    public void ComputePrintableHeightPx_Landscape_SwapsWidthAndHeight()
    {
        var portrait = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "A4", Landscape = false });
        var landscape = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "A4", Landscape = true });

        Assert.NotEqual(portrait, landscape);
    }

    [Fact]
    public void ComputePrintableHeightPx_UnknownPageSize_FallsBackToA4()
    {
        var fallback = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "NotARealSize" });
        var a4 = PaginationPlanner.ComputePrintableHeightPx(new RenderingOptions { PageSize = "A4" });

        Assert.Equal(a4, fallback);
    }

    [Fact]
    public void ComputePrintableHeightPx_NullOptions_DoesNotThrow()
    {
        var height = PaginationPlanner.ComputePrintableHeightPx(null);

        Assert.True(height > 0);
    }
}
