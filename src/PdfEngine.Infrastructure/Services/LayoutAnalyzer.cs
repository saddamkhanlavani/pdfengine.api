using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class LayoutAnalyzer : ILayoutAnalyzer
{
    public Task ExecuteAsync(RenderingContext context, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var html = context.Html;
        var layout = context.Layout;

        if (string.IsNullOrEmpty(html))
        {
            return Task.CompletedTask;
        }

        // 1. Analyze CSS patterns in stylesheets/style tags or inline attributes
        cancellationToken.ThrowIfCancellationRequested();

        if (html.Contains("backdrop-filter", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: 'backdrop-filter' detected. Backdrop filters are not supported in print PDF layouts. Use standard semi-transparent background colors instead.");
        }
        if (html.Contains("mix-blend-mode", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: 'mix-blend-mode' detected. Blend modes have unstable support in print rendering. Use pre-rendered images or flat opacity layers.");
        }
        if (html.Contains("filter:", StringComparison.OrdinalIgnoreCase) || html.Contains("filter :", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: 'filter' detected. Graphical CSS filters can cause severe layout rendering lag or print layout inconsistencies. Use flat or pre-filtered assets.");
        }
        if (html.Contains("mask-image", StringComparison.OrdinalIgnoreCase) || 
            html.Contains("-webkit-mask", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(html, @"\bmask\s*:", RegexOptions.IgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: CSS Complex Masking detected. Mask properties are unstable in print engines. Use transparent PNG assets instead.");
        }
        if (html.Contains("container-type", StringComparison.OrdinalIgnoreCase) || html.Contains("container-name", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: CSS Container Queries detected. Print engines do not always evaluate container queries correctly. Use standard media queries or flexbox layouts.");
        }
        if (html.Contains("position: fixed", StringComparison.OrdinalIgnoreCase) || html.Contains("position:fixed", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: 'position: fixed' detected. Fixed position elements can overlap content or duplicate unpredictably across pages. Use margin-top/bottom or header/footer templates.");
        }
        
        if (!html.Contains("@page", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: Missing '@page' margin rule. Add '@page { size: A4; margin: 12mm; }' to ensure layouts do not crop at physical boundaries.");
        }

        if (html.Contains("<table", StringComparison.OrdinalIgnoreCase) && !html.Contains("table-header-group", StringComparison.OrdinalIgnoreCase))
        {
            layout.LayoutWarnings.Add("Actionable CSS Suggestion: Table detected without 'table-header-group'. To repeat table headers across pages, add: 'thead { display: table-header-group; }'.");
        }

        // Map warnings to shared diagnostics
        foreach (var warning in layout.LayoutWarnings)
        {
            context.Diagnostics.Warnings.Add(warning);
        }

        return Task.CompletedTask;
    }
}
