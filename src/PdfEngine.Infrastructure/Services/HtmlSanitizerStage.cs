using System;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ganss.Xss;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

/// <summary>
/// Parses and sanitizes untrusted HTML before it ever reaches Chromium. This is the
/// engine's actual security boundary against script injection — it must remove
/// dangerous content, not merely report on it.
/// </summary>
public class HtmlSanitizerStage : IHtmlSanitizerStage
{
    private readonly ILogger<HtmlSanitizerStage> _logger;
    private readonly HtmlSanitizer _strictSanitizer;
    private readonly HtmlSanitizer _scriptsAllowedSanitizer;

    public HtmlSanitizerStage(ILogger<HtmlSanitizerStage> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _strictSanitizer = BuildSanitizer(allowScripts: false);
        _scriptsAllowedSanitizer = BuildSanitizer(allowScripts: true);
    }

    /// <summary>
    /// Maximum element nesting depth accepted. Chromium's own parser caps nesting in the
    /// same order of magnitude, and real documents — even generated ones — sit far below
    /// this, so the limit rejects attacks without rejecting legitimate work.
    /// </summary>
    internal const int MaxNestingDepth = 512;

    // Void elements never open a level, and neither do self-closing tags, comments,
    // doctypes or closing tags. Counting them would produce false depth on ordinary
    // markup (a long run of <br> or <img> would look like deep nesting).
    private static readonly System.Collections.Generic.HashSet<string> VoidElements =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

    private static readonly Regex TagPattern =
        new(@"<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9:-]*)([^>]*?)(/?)\s*>", RegexOptions.Compiled);

    /// <summary>
    /// Maximum open-element depth of the raw markup. Intentionally a string scan rather
    /// than a DOM walk: this guards the parser, so it must not depend on the parser.
    /// Unbalanced markup only ever makes the measured depth larger, which fails safe.
    /// </summary>
    internal static int MeasureMaxNestingDepth(string html)
    {
        int depth = 0, max = 0;
        foreach (Match m in TagPattern.Matches(html))
        {
            var name = m.Groups[2].Value;
            if (VoidElements.Contains(name)) continue;

            if (m.Groups[1].Value == "/")
            {
                if (depth > 0) depth--;
            }
            else if (m.Groups[4].Value != "/")   // not self-closing
            {
                depth++;
                if (depth > max) max = depth;
            }
        }
        return max;
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

        // MUST run before the sanitizer touches the document. AngleSharp's Node.Normalize
        // recurses once per nesting level, so deeply nested HTML overflows the stack —
        // measured: ~6,000 nested <div>s produced "Stack overflow. Repeat 4009 times: at
        // AngleSharp.Dom.Node.Normalize()" and killed the ENTIRE API process, taking every
        // other tenant's in-flight render with it. A stack overflow cannot be caught in
        // .NET, so there is no try/catch that makes this safe after the fact; the only
        // defence is refusing the document up front. Counting depth on the raw string is
        // deliberate — it needs no parser and therefore cannot itself overflow.
        var depth = MeasureMaxNestingDepth(html);
        if (depth > MaxNestingDepth)
        {
            throw new InvalidOperationException(
                $"HTML nesting depth {depth} exceeds the maximum of {MaxNestingDepth}. "
                + "Deeply nested markup can exhaust the parser stack, so the document was "
                + "refused rather than risk terminating the renderer.");
        }

        try
        {
            var allowScripts = context.Options?.AllowScripts ?? false;
            var sanitizer = allowScripts ? _scriptsAllowedSanitizer : _strictSanitizer;

            var removedCount = 0;
            EventHandler<Ganss.Xss.RemovingTagEventArgs> onRemovingTag = (s, e) => removedCount++;
            EventHandler<Ganss.Xss.RemovingAttributeEventArgs> onRemovingAttribute = (s, e) => removedCount++;
            EventHandler<Ganss.Xss.RemovingStyleEventArgs> onRemovingStyle = (s, e) => removedCount++;
            EventHandler<Ganss.Xss.RemovingAtRuleEventArgs> onRemovingAtRule = (s, e) => removedCount++;

            sanitizer.RemovingTag += onRemovingTag;
            sanitizer.RemovingAttribute += onRemovingAttribute;
            sanitizer.RemovingStyle += onRemovingStyle;
            sanitizer.RemovingAtRule += onRemovingAtRule;

            string sanitized;
            try
            {
                sanitized = sanitizer.SanitizeDocument(html);
            }
            finally
            {
                sanitizer.RemovingTag -= onRemovingTag;
                sanitizer.RemovingAttribute -= onRemovingAttribute;
                sanitizer.RemovingStyle -= onRemovingStyle;
                sanitizer.RemovingAtRule -= onRemovingAtRule;
            }

            if (removedCount > 0)
            {
                context.Diagnostics?.Warnings.Add(
                    $"Sanitizer Notice: Removed {removedCount} disallowed tag/attribute/style occurrence(s) (event handlers, unsafe URIs{(allowScripts ? "" : ", or <script> tags")}) from the document.");
            }

            if (allowScripts)
            {
                context.Diagnostics?.Warnings.Add(
                    "Sanitizer Notice: AllowScripts is enabled for this render — <script> tags were preserved. Only use this for trusted document sources, not third-party/user-submitted HTML.");

                // Verified by direct testing: the sanitizer's HTML serializer
                // re-escapes <script> body text the same way it would ordinary
                // page text — `=>` comes out as `=&gt;`, `a < b` as `a &lt; b`.
                // Per the HTML5 spec, script/style element content is "raw text"
                // and must never be entity-escaped; violating that silently turns
                // any inline script using arrow functions or comparison operators
                // into a JS syntax error, which fails completely silently (no
                // exception anywhere in this pipeline — the script just never
                // runs). That reproduced exactly as a WaitForFunction/chart-ready
                // flag that never fires and hangs for the full render timeout.
                // <script src="..."> tags have no body to decode — harmless no-op.
                sanitized = DecodeScriptBodies(sanitized);
            }

            html = EnsureDoctype(sanitized);
            html = EnsureViewport(html);

            context.Html = html;
        }
        catch (Exception ex)
        {
            // Fail closed: if sanitization itself throws, do not forward the original,
            // unsanitized HTML — that would silently defeat the security boundary.
            _logger.LogError(ex, "HTML Sanitizer Stage failed to process document; rejecting unsanitized content.");
            context.Diagnostics?.Warnings.Add($"Sanitizer Error: Document could not be safely sanitized ({ex.Message}). Rendering blocked.");
            throw;
        }

        return Task.CompletedTask;
    }

    private static readonly Regex ScriptBodyPattern = new(
        @"(<script\b[^>]*>)(.*?)(</script\s*>)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string DecodeScriptBodies(string html) =>
        ScriptBodyPattern.Replace(html, m =>
            m.Groups[2].Value.Length == 0
                ? m.Value
                : m.Groups[1].Value + WebUtility.HtmlDecode(m.Groups[2].Value) + m.Groups[3].Value);

    private static HtmlSanitizer BuildSanitizer(bool allowScripts)
    {
        var sanitizer = new HtmlSanitizer();

        // Inline SVG graphics are a first-class rendering need (charts, icons, diagrams),
        // not just plain-text documents. "style" is a PDF-rendering essential (that's how
        // print CSS/@page rules arrive) and isn't in the library's body-content-oriented
        // default allowlist.
        foreach (var tag in new[]
        {
            "html", "head", "body", "title", "style", "meta",
            "svg", "path", "g", "rect", "circle", "ellipse", "line", "polyline", "polygon",
            "text", "tspan", "defs", "clippath", "lineargradient", "radialgradient", "stop",
            "use", "symbol", "marker", "pattern", "filter", "fegaussianblur", "feoffset",
            "femerge", "femergenode", "fecolormatrix", "fecomposite", "feflood", "feblend",
            "foreignobject", "canvas"
        })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        // Reusable page templates and print CSS live in <link rel="stylesheet">.
        sanitizer.AllowedTags.Add("link");

        // Script execution is off by default (the security-first posture) — a document
        // with <script> stripped is safe against arbitrary code execution, but it also
        // means chart/graph libraries (Chart.js, D3, canvas-drawing code) can never run,
        // since the tag carrying their logic is removed along with everything else. When
        // the caller explicitly opts in via AllowScripts (asserting the HTML source is
        // trusted), allow the tag through — inline event handlers and javascript: URIs
        // remain blocked either way via AllowedAttributes/AllowedSchemes below.
        if (allowScripts)
        {
            sanitizer.AllowedTags.Add("script");
            sanitizer.AllowedAttributes.Add("src");
            sanitizer.AllowedAttributes.Add("async");
            sanitizer.AllowedAttributes.Add("defer");
            sanitizer.AllowedAttributes.Add("integrity");
            sanitizer.AllowedAttributes.Add("crossorigin");
        }

        foreach (var attr in new[]
        {
            "style", "class", "id", "dir", "lang", "colspan", "rowspan", "viewbox",
            "d", "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
            "cx", "cy", "r", "rx", "ry", "x", "y", "x1", "y1", "x2", "y2", "width", "height",
            "transform", "points", "xmlns", "preserveaspectratio", "gradientunits",
            "gradienttransform", "offset", "stop-color", "stop-opacity", "clip-path",
            "opacity", "fill-opacity", "font-family", "font-size", "text-anchor",
            "rel", "media", "type", "role", "aria-hidden", "aria-label",
            "name", "content", "charset", "http-equiv", "property",
            "data-pdfengine-pageref"
        })
        {
            sanitizer.AllowedAttributes.Add(attr);
        }

        // Embedded fonts/images and same-origin resources arrive as data: URIs;
        // http/https resources go through the SSRF-guarded resource loader.
        sanitizer.AllowedSchemes.Add("data");

        // HtmlSanitizer's default AllowedAtRules is just {Style, Namespace} — verified
        // by direct testing to silently strip @import, @font-face, @page, and
        // @media from every <style> block. That meant every Google Fonts @import,
        // every self-hosted @font-face, and every author-defined @page
        // margin/@media print rule was quietly deleted before Chromium ever saw
        // it, with no warning distinguishing it from a stripped <script> tag.
        // These four are inert CSS declarations, not executable content — the
        // network fetch an @import/@font-face triggers still goes through the
        // same SSRF-pinned resource loader as every other request, so allowing
        // the rule through here doesn't bypass that boundary.
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Import);
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.FontFace);
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Page);
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Supports);

        // The gradient-text technique (background: linear-gradient(...) clipped to
        // the glyph shapes) is one of the most common modern headline treatments —
        // and standard practice still pairs the unprefixed background-clip (already
        // allowed) with the vendor-prefixed properties for broad support. Verified
        // by direct testing: -webkit-background-clip/-webkit-text-fill-color were
        // silently stripped, so every gradient-text heading rendered as opaque
        // black text sitting on top of a solid gradient rectangle instead of the
        // intended gradient-colored glyphs. Same class of gap as the at-rules
        // above — an inert style declaration, not executable content.
        foreach (var cssProp in new[] { "-webkit-background-clip", "-webkit-text-fill-color", "-webkit-line-clamp", "-webkit-box-orient" })
        {
            sanitizer.AllowedCssProperties.Add(cssProp);
        }

        // The page-geometry descriptors of `@page`. Exactly the same gap as the
        // vendor-prefixed properties above, and it silently broke the headline
        // page-geometry feature: allowing the @page AT-RULE through is not enough,
        // because each DECLARATION inside it is filtered separately. `margin` was
        // already allowed (it is a normal CSS property) while `size` was not, so
        // `@page { size: A5 landscape; margin: 10mm }` reached Chromium as
        // `@page { margin: 10mm }`. Measured: the margin applied and the size did
        // not, and every document — A5, landscape, custom mm dimensions alike —
        // came out A4. These descriptors are inert layout metadata, never
        // executable content.
        foreach (var pageProp in new[] { "size", "marks", "bleed" })
        {
            sanitizer.AllowedCssProperties.Add(pageProp);
        }

        // Only allow <link> through when it's actually a stylesheet reference — anything
        // else (rel=import, preload of scripts, etc.) is dropped by leaving it unlisted.
        sanitizer.PostProcessNode += (s, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement el && el.TagName.Equals("link", StringComparison.OrdinalIgnoreCase))
            {
                var rel = el.GetAttribute("rel");
                if (!string.Equals(rel, "stylesheet", StringComparison.OrdinalIgnoreCase))
                {
                    el.Remove();
                }
            }
        };

        return sanitizer;
    }

    private static string EnsureDoctype(string html)
    {
        if (!html.TrimStart().StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
        {
            html = "<!DOCTYPE html>\n" + html;
        }

        return html;
    }

    private static string EnsureViewport(string html)
    {
        if (html.Contains("viewport", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var viewportTag = "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">";
        var headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx != -1)
        {
            return html.Insert(headIdx + 6, $"\n{viewportTag}\n");
        }

        var htmlIdx = html.IndexOf("<html>", StringComparison.OrdinalIgnoreCase);
        if (htmlIdx != -1)
        {
            return html.Insert(htmlIdx + 6, $"\n<head>{viewportTag}</head>\n");
        }

        return $"{viewportTag}\n" + html;
    }
}
