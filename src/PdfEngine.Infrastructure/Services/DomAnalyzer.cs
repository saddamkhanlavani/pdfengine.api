using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class DomAnalyzer : IDomAnalyzer
{
    // HTML5 elements that are always self-closing and never carry a trailing "/>" in
    // valid markup. Without this list, ordinary tags like <img> or <br> were counted
    // as "opened" but never "closed", producing false unclosed-tag warnings on every
    // standard HTML5 document.
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    };

    public Task ExecuteAsync(RenderingContext context, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var html = context.Html;
        var model = context.Model;

        if (string.IsNullOrEmpty(html))
        {
            model.ParserWarnings.Add("Empty HTML payload provided.");
            return Task.CompletedTask;
        }

        // 1. Compute element counts and nesting depth
        int elementCount = 0;
        int maxDepth = 0;
        int currentDepth = 0;
        bool hasUnclosed = false;

        // Fast element scanning state machine
        for (int i = 0; i < html.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (html[i] == '<')
            {
                if (i + 1 < html.Length)
                {
                    char next = html[i + 1];
                    if (next == '/') // Closing tag e.g. </div>
                    {
                        currentDepth = Math.Max(0, currentDepth - 1);
                        i++;
                    }
                    else if (next == '!' || next == '?') // Comments or doctype/xml declaration
                    {
                        // Skip tag comment block
                        int closeIdx = html.IndexOf('>', i);
                        if (closeIdx != -1) i = closeIdx;
                    }
                    else // Opening tag e.g. <div>
                    {
                        elementCount++;
                        currentDepth++;
                        if (currentDepth > maxDepth)
                        {
                            maxDepth = currentDepth;
                        }

                        // Check self-closing tag e.g. <img src="" /> or a void element
                        // written without a trailing slash, e.g. <img src=""> or <br>.
                        int endTagIdx = html.IndexOf('>', i);
                        if (endTagIdx != -1)
                        {
                            bool selfClosingSlash = endTagIdx > 0 && html[endTagIdx - 1] == '/';
                            var tagName = ExtractTagName(html, i + 1, endTagIdx);
                            if (selfClosingSlash || VoidElements.Contains(tagName))
                            {
                                currentDepth = Math.Max(0, currentDepth - 1);
                            }
                            i = endTagIdx;
                        }
                    }
                }
            }
        }

        model.NodeCount = elementCount;
        model.MaxDepth = maxDepth;

        if (currentDepth > 0)
        {
            hasUnclosed = true;
            model.ParserWarnings.Add($"HTML Structure warning: Detected {currentDepth} unclosed nested tag(s) in the layout tree.");
        }

        model.HasUnclosedTags = hasUnclosed;

        model.HasTemplatePlaceholders = DetectTemplatePlaceholders(html, model.ParserWarnings);

        // Map warnings to shared diagnostics
        foreach (var warning in model.ParserWarnings)
        {
            context.Diagnostics.Warnings.Add(warning);
        }

        // Evaluate warning thresholds
        if (model.MaxDepth > 12)
        {
            context.Diagnostics.Warnings.Add($"DOM Warning: HTML structure depth ({model.MaxDepth}) exceeds the threshold size of 12. Highly nested layouts can degrade compiler performance.");
        }

        return Task.CompletedTask;
    }

    private static string ExtractTagName(string html, int start, int endTagIdx)
    {
        int end = start;
        while (end < endTagIdx && !char.IsWhiteSpace(html[end]) && html[end] != '/' && html[end] != '>')
        {
            end++;
        }
        return html.Substring(start, end - start);
    }

    private static bool DetectTemplatePlaceholders(string html, List<string> warnings)
    {
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

        return foundTemplate;
    }
}
