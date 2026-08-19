using System.Threading.Tasks;
using PdfEngine.Infrastructure.Services;
using Xunit;

namespace PdfEngine.UnitTests;

public class RowspanProtectionTests
{
    [Fact]
    public async Task ProtectRowspanContinuationsAsync_ContinuationRows_GetBreakBeforeAvoid()
    {
        var html = @"<html><body><table>
            <tr><td rowspan=""3"">Spans 3 rows</td><td>Row 1</td></tr>
            <tr><td>Row 2</td></tr>
            <tr><td>Row 3</td></tr>
            <tr><td>Row 4</td><td>Row 4</td></tr>
        </table></body></html>";

        var result = await PaginationPlanner.ProtectRowspanContinuationsAsync(html);

        // Row 1 (opens the span) should NOT be marked — nothing above it needs protecting.
        Assert.DoesNotContain("break-before", ExtractRowStyle(result, "Spans 3 rows"));

        Assert.Contains("break-before:avoid", ExtractRowStyle(result, "Row 2"));
        Assert.Contains("break-before:avoid", ExtractRowStyle(result, "Row 3"));

        // Row 4 is past the span entirely — must not be marked.
        Assert.DoesNotContain("break-before", ExtractRowStyle(result, "Row 4"));
    }

    [Fact]
    public async Task ProtectRowspanContinuationsAsync_NoRowspan_ReturnsHtmlUnchangedStructurally()
    {
        var html = "<html><body><table><tr><td>A</td></tr><tr><td>B</td></tr></table></body></html>";

        var result = await PaginationPlanner.ProtectRowspanContinuationsAsync(html);

        Assert.DoesNotContain("break-before", result);
        Assert.Contains("A", result);
        Assert.Contains("B", result);
    }

    [Fact]
    public async Task ProtectRowspanContinuationsAsync_ColspanAndRowspanCombined_TracksColumnsCorrectly()
    {
        var html = @"<html><body><table>
            <tr><td colspan=""2"" rowspan=""2"">Wide and tall</td><td>CellC</td></tr>
            <tr><td>CellD</td></tr>
            <tr><td>CellE</td><td>CellF</td><td>CellG</td></tr>
        </table></body></html>";

        var result = await PaginationPlanner.ProtectRowspanContinuationsAsync(html);

        Assert.Contains("break-before:avoid", ExtractRowStyle(result, "CellD"));
        Assert.DoesNotContain("break-before", ExtractRowStyle(result, "CellE"));
    }

    private static string ExtractRowStyle(string html, string cellText)
    {
        var cellIdx = html.IndexOf(cellText, System.StringComparison.Ordinal);
        Assert.True(cellIdx >= 0, $"Expected to find cell text '{cellText}' in output.");
        var rowStart = html.LastIndexOf("<tr", cellIdx, System.StringComparison.Ordinal);
        Assert.True(rowStart >= 0, $"Expected an enclosing <tr> before '{cellText}'.");
        var rowTagEnd = html.IndexOf('>', rowStart);
        return html.Substring(rowStart, rowTagEnd - rowStart);
    }
}
