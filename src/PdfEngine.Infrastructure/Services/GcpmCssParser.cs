using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEngine.Application.DTOs;

namespace PdfEngine.Infrastructure.Services;

/// <summary>
/// Extracts CSS Generated Content for Paged Media (GCPM) constructs from raw CSS text.
///
/// These MUST be parsed from the raw source rather than through the browser's CSSOM.
/// Chromium does not implement <c>string-set</c>, <c>target-counter()</c>,
/// <c>leader()</c> or <c>@page</c> margin boxes, and an engine drops declarations and
/// at-rules it does not recognise BEFORE they ever reach CSSOM — so
/// <c>getComputedStyle</c> and <c>cssRules</c> report nothing at all for them. Reading the
/// stylesheet text ourselves is the only way to see what the author actually wrote.
///
/// This class only PARSES. Applying the results (DOM injection, per-page stamping) is done
/// by the pagination planner and the PDF post-processor respectively, so the parsing is
/// unit-testable without a browser.
/// </summary>
internal static class GcpmCssParser
{
    // Hard backstop. The patterns below can backtrack on adversarial CSS, and this parser
    // runs on caller-supplied input on every render — a hang here is a denial of service,
    // not a slow parse. A timeout degrades to "these features do nothing for this
    // document", which is the correct failure direction.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    // `h1 { string-set: chapter content(); }` — the selector and the string name.
    private static readonly Regex StringSetPattern = new(
        @"(?<selector>[^{}@]+?)\{[^{}]*?string-set\s*:\s*(?<name>[A-Za-z_][\w-]*)\s+(?<value>content\s*\(\s*[a-z-]*\s*\)|""[^""]*""|'[^']*')",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    // `a::after { content: target-counter(attr(href), page); }`
    private static readonly Regex TargetCounterPattern = new(
        @"(?<selector>[^{}@]+?)\{[^{}]*?content\s*:[^;}]*?target-counter\s*\(\s*(?<target>attr\s*\(\s*href\s*\)|""[^""]*""|'[^']*')\s*,\s*page\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    // `.toc a::after { content: leader('.'); }`
    private static readonly Regex LeaderPattern = new(
        @"(?<selector>[^{}@]+?)\{[^{}]*?content\s*:[^;}]*?leader\s*\(\s*(?<char>""[^""]*""|'[^']*'|dotted|solid|space)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    // `.fn { float: footnote }` — the element to lift out of the text flow (T1-5).
    // Chromium renders this content INLINE exactly where it was authored (measured
    // 2026-08-18: the marker landed 16% into page 1, not at the page bottom), so the
    // engine has to relocate it. Nothing here is a pass-through.
    private static readonly Regex FootnotePattern = new(
        @"(?<selector>[^{}@]+?)\{[^{}]*?float\s*:\s*footnote\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    // `figure { float: top }` / `float: bottom` — GCPM page floats (T1-8).
    // `top`/`bottom` are not valid values for the CSS `float` property, so this cannot
    // collide with the ordinary `float: left|right|none|inline-start|inline-end` that
    // appears in most stylesheets. Measured 2026-08-18: Chromium renders BOTH exactly
    // where authored (38% down the page, indistinguishable from no float at all).
    private static readonly Regex PageFloatPattern = new(
        @"(?<selector>[^{}@]+?)\{[^{}]*?float\s*:\s*(?<edge>top|bottom)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    // `.fn::footnote-call { content: counter(footnote); font-size: 7pt }` and the
    // matching `::footnote-marker`. The SELECTOR is deliberately ignored: the two
    // pseudo-elements are collected document-wide and applied to every footnote, because
    // per-selector call styling is exotic and the alternative is a second cascade
    // implementation. Documented as a limitation rather than silently half-supported.
    private static readonly Regex FootnotePseudoPattern = new(
        @"[^{}@]*?::?footnote-(?<which>call|marker)\s*\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    // `@page { @footnote { border-top: 1px solid #999; width: 30% } }` — the footnote
    // area's own box, matched inside an @page block alongside the margin boxes.
    private static readonly Regex FootnoteAreaPattern = new(
        @"@footnote\s*\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // Per-page footnote numbering. Recognised so it can be REPORTED as unsupported —
    // the call marker is baked into the rendered page before the page it landed on is
    // known, so restarting the counter per page would need a further re-render.
    private static readonly Regex PerPageFootnoteCounterPattern = new(
        @"counter-reset\s*:[^;}]*\bfootnote\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // `@page :first { @top-center { content: string(chapter); font-size: 9pt } }`
    // Nested braces mean a plain regex cannot match the whole @page block reliably, so
    // the block is located and then brace-matched by hand.
    // `@page { }`, `@page :first { }`, `@page cover { }`, `@page cover:first { }`.
    // The NAME group is what makes T1-7 possible; the pseudo group is what T1-1/T1-4
    // already used, and both have to be readable from the same rule.
    private static readonly Regex PageRuleStart = new(
        @"@page\s*(?<name>[A-Za-z_][\w-]*)?\s*(?<selector>:[a-z]+)?\s*\{",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // `.cover { page: cover }` — the box-to-named-page binding (T1-7).
    // `page\s*:` cannot match `page-break-before:` (a hyphen follows `page`, not a colon),
    // which is the one collision that would matter: forced breaks are on a large share of
    // real documents and treating one as a named page would re-render the whole section.
    private static readonly Regex NamedPageUsePattern = new(
        @"(?<selector>[^{}@]+?)\{[^{}]*?\bpage\s*:\s*(?<name>[A-Za-z_][\w-]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex MarginBoxPattern = new(
        @"@(?<box>top-left|top-center|top-right|bottom-left|bottom-center|bottom-right)\s*\{(?<body>[^{}]*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Regex DeclarationPattern = new(
        @"(?<prop>[a-z-]+)\s*:\s*(?<value>[^;}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>Selector plus the string name it assigns.</summary>
    internal sealed record StringSetRule(string Selector, string Name);

    /// <summary>Selector plus how the reference target is obtained.</summary>
    internal sealed record TargetCounterRule(string Selector, string TargetAttr, bool Before);

    internal sealed record LeaderRule(string Selector, string Character, bool Before);

    /// <summary>A selector whose elements carry <c>float: footnote</c>.</summary>
    internal sealed record FootnoteRule(string Selector);

    /// <summary>A selector whose elements carry <c>float: top</c> or <c>float: bottom</c>.</summary>
    internal sealed record PageFloatRule(string Selector, string Edge);

    /// <summary>A selector bound to a named page by <c>page: &lt;name&gt;</c>.</summary>
    internal sealed record NamedPageUseRule(string Selector, string Name);

    /// <summary>Styling declared on <c>::footnote-call</c> or <c>::footnote-marker</c>.</summary>
    internal sealed class FootnotePseudoStyle
    {
        /// <summary>The raw <c>content:</c> expression, e.g. <c>counter(footnote, lower-roman)</c>.</summary>
        public string? Content { get; set; }
        public double? FontSizePt { get; set; }
        public string? Color { get; set; }
    }

    internal sealed class GcpmDocument
    {
        public List<StringSetRule> StringSets { get; } = new();
        public List<TargetCounterRule> TargetCounters { get; } = new();
        public List<LeaderRule> Leaders { get; } = new();
        public List<MarginBoxRequest> MarginBoxes { get; } = new();

        // T1-5 footnotes.
        public List<FootnoteRule> Footnotes { get; } = new();

        // T1-8 page floats.
        public List<PageFloatRule> PageFloats { get; } = new();

        // T1-7 named pages: the `@page <name>` definitions and the boxes bound to them.
        public Dictionary<string, NamedPageDefinition> NamedPages { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<NamedPageUseRule> NamedPageUses { get; } = new();

        /// <summary>The plain `@page { }` geometry, needed to CANCEL a `:first` rule on
        /// the parts of a stitched document that are not the first.</summary>
        public NamedPageDefinition? DefaultPage { get; set; }

        /// <summary>Which of `first` / `left` / `right` declare page geometry.</summary>
        public HashSet<string> PseudoPageGeometry { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public FootnotePseudoStyle? FootnoteCall { get; set; }
        public FootnotePseudoStyle? FootnoteMarker { get; set; }
        public FootnoteAreaRequest? FootnoteArea { get; set; }

        /// <summary>True when the document asks for per-page footnote numbering.</summary>
        public bool RequestsPerPageFootnoteNumbering { get; set; }

        public bool IsEmpty => StringSets.Count == 0 && TargetCounters.Count == 0
                            && Leaders.Count == 0 && MarginBoxes.Count == 0
                            && Footnotes.Count == 0 && PageFloats.Count == 0
                            && NamedPageUses.Count == 0;
    }

    private static readonly Regex StyleBlockPattern = new(
        @"<style[^>]*>(?<css>.*?)</style>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexTimeout);

    /// <summary>
    /// Concatenates the document's <c>&lt;style&gt;</c> contents.
    ///
    /// Scanning the whole HTML string instead was measurably wrong: a selector capture
    /// begins at the previous <c>}</c> or <c>;</c>, so the FIRST rule in a stylesheet
    /// captured the surrounding markup and yielded the selector
    /// <c>"&lt;html&gt;&lt;head&gt;&lt;style&gt;h1"</c>, which matches nothing — the rule
    /// silently did nothing. Fixtures with a preceding rule hid it. Externally linked
    /// stylesheets are out of scope and documented as such.
    /// </summary>
    private static string ExtractStyleCss(string html)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Match m in StyleBlockPattern.Matches(html))
            sb.Append(m.Groups["css"].Value).Append('\n');
        // No <style> block means no CSS, so there is nothing to find. Falling back to the
        // whole document was measurably harmful: a 3.7MB style-free document put every
        // pattern below across all 3.7MB of markup and the request timed out. Returning
        // empty is both correct and the fast path for the majority of documents.
        return sb.ToString();
    }

    internal static GcpmDocument Parse(string? html)
    {
        var doc = new GcpmDocument();
        if (string.IsNullOrEmpty(html)) return doc;

        var css = ExtractStyleCss(html);
        if (css.Length == 0) return doc;

        // Cheap ordinal pre-check before any regex. These constructs are rare; almost
        // every document should exit here having paid a handful of substring scans rather
        // than several backtracking-capable patterns over the whole stylesheet.
        //
        // Each scan is gated on its OWN marker rather than on the union. `float` is why:
        // ordinary `float: left` appears in a large share of real stylesheets, so a union
        // gate would have made every one of them pay for all five patterns to find the
        // page floats that almost none of them declare.
        var hasStringSet = css.IndexOf("string-set", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasTargetCounter = css.IndexOf("target-counter", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasLeader = css.IndexOf("leader", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasFootnote = css.IndexOf("footnote", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasFloat = css.IndexOf("float", StringComparison.OrdinalIgnoreCase) >= 0;
        var hasPageRule = css.IndexOf("@page", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!hasStringSet && !hasTargetCounter && !hasLeader && !hasFootnote
            && !hasFloat && !hasPageRule)
        {
            return doc;
        }

        // Margin boxes must be extracted before the generic scans: their bodies contain
        // `content:` declarations that would otherwise be misread as ordinary rules.
        var withoutPageRules = ExtractMarginBoxes(css, doc);

        // `::footnote-call` / `::footnote-marker` bodies carry their own `content:`
        // declarations, so they come out before the generic scans for the same reason
        // margin boxes do.
        withoutPageRules = ExtractFootnotePseudoElements(withoutPageRules, doc);

        if (hasStringSet)
        foreach (Match m in StringSetPattern.Matches(withoutPageRules))
        {
            var selector = CleanSelector(m.Groups["selector"].Value, out _);
            if (selector.Length > 0)
                doc.StringSets.Add(new StringSetRule(selector, m.Groups["name"].Value));
        }

        if (hasTargetCounter)
        foreach (Match m in TargetCounterPattern.Matches(withoutPageRules))
        {
            var selector = CleanSelector(m.Groups["selector"].Value, out var before);
            if (selector.Length == 0) continue;
            var target = m.Groups["target"].Value;
            // `attr(href)` is by far the common form; a literal selector is also legal.
            var attr = target.Contains("attr", StringComparison.OrdinalIgnoreCase)
                ? "href" : Unquote(target);
            doc.TargetCounters.Add(new TargetCounterRule(selector, attr, before));
        }

        if (hasLeader)
        foreach (Match m in LeaderPattern.Matches(withoutPageRules))
        {
            var selector = CleanSelector(m.Groups["selector"].Value, out var before);
            if (selector.Length == 0) continue;
            var raw = m.Groups["char"].Value;
            var ch = raw.Equals("dotted", StringComparison.OrdinalIgnoreCase) ? "."
                   : raw.Equals("solid", StringComparison.OrdinalIgnoreCase) ? "_"
                   : raw.Equals("space", StringComparison.OrdinalIgnoreCase) ? " "
                   : Unquote(raw);
            if (ch.Length == 0) ch = ".";
            doc.Leaders.Add(new LeaderRule(selector, ch, before));
        }

        if (hasFootnote)
        foreach (Match m in FootnotePattern.Matches(withoutPageRules))
        {
            // A footnote selector must never carry a pseudo-element: `float` applies to
            // the element itself, and CleanSelector's ::before/::after stripping would
            // silently widen the selector to the whole element if one were written.
            var selector = CleanSelector(m.Groups["selector"].Value, out _);
            if (selector.Length > 0 && !doc.Footnotes.Any(f => f.Selector == selector))
                doc.Footnotes.Add(new FootnoteRule(selector));
        }

        if (hasPageRule || css.IndexOf("page", StringComparison.OrdinalIgnoreCase) >= 0)
        foreach (Match m in NamedPageUsePattern.Matches(withoutPageRules))
        {
            var selector = CleanSelector(m.Groups["selector"].Value, out _);
            if (selector.Length == 0) continue;
            var name = m.Groups["name"].Value;
            // `page: auto` is the CSS initial value — it names no page and must not
            // trigger a separate render.
            if (name.Equals("auto", StringComparison.OrdinalIgnoreCase)) continue;
            if (!doc.NamedPageUses.Any(r => r.Selector == selector && r.Name == name))
                doc.NamedPageUses.Add(new NamedPageUseRule(selector, name));
        }

        if (hasFloat)
        foreach (Match m in PageFloatPattern.Matches(withoutPageRules))
        {
            var selector = CleanSelector(m.Groups["selector"].Value, out _);
            if (selector.Length == 0) continue;
            var edge = m.Groups["edge"].Value.ToLowerInvariant();
            if (!doc.PageFloats.Any(f => f.Selector == selector && f.Edge == edge))
                doc.PageFloats.Add(new PageFloatRule(selector, edge));
        }

        return doc;
    }

    /// <summary>
    /// Pulls the two footnote pseudo-elements out and returns the CSS without them.
    /// </summary>
    private static string ExtractFootnotePseudoElements(string css, GcpmDocument doc)
    {
        if (css.IndexOf("footnote-", StringComparison.OrdinalIgnoreCase) < 0) return css;

        foreach (Match m in FootnotePseudoPattern.Matches(css))
        {
            var style = new FootnotePseudoStyle();
            foreach (Match d in DeclarationPattern.Matches(m.Groups["body"].Value))
            {
                var value = d.Groups["value"].Value.Trim();
                switch (d.Groups["prop"].Value.ToLowerInvariant())
                {
                    case "content": style.Content = value; break;
                    case "font-size": style.FontSizePt = ParseLengthPt(value, 9); break;
                    case "color": style.Color = value; break;
                }
            }

            if (m.Groups["which"].Value.Equals("call", StringComparison.OrdinalIgnoreCase))
                doc.FootnoteCall = style;
            else
                doc.FootnoteMarker = style;
        }

        return FootnotePseudoPattern.Replace(css, string.Empty);
    }

    /// <summary>
    /// Pulls `@page` margin boxes out and returns the CSS with those blocks removed, so
    /// later scans cannot re-match declarations that live inside them.
    /// </summary>
    private static string ExtractMarginBoxes(string css, GcpmDocument doc)
    {
        var result = new System.Text.StringBuilder(css.Length);
        var cursor = 0;

        while (true)
        {
            var start = PageRuleStart.Match(css, cursor);
            if (!start.Success) break;

            var open = start.Index + start.Length - 1;
            var end = FindMatchingBrace(css, open);
            if (end < 0) break;

            var body = css.Substring(open + 1, end - open - 1);
            var selector = start.Groups["selector"].Success
                ? start.Groups["selector"].Value.TrimStart(':').ToLowerInvariant()
                : null;

            // `@page cover { size: A4 landscape; margin: 50mm }` (T1-7). A named page's
            // geometry cannot be applied by stamping — it changes layout — so it is
            // recorded here and used to render that section separately.
            if (start.Groups["name"].Success && start.Groups["name"].Value.Length > 0)
            {
                ExtractNamedPage(start.Groups["name"].Value, body, doc);
            }
            else
            {
                // The plain and pseudo `@page` rules. Their geometry matters to T1-7: a
                // stitched document renders each run separately, so an uncancelled
                // `@page :first` would apply to the first page of EVERY part.
                var target = new NamedPageDefinition { Name = selector ?? string.Empty };
                ReadPageGeometry(body, target);
                if (selector == null)
                {
                    if (target.ChangesGeometry) doc.DefaultPage = target;
                }
                else if (target.ChangesGeometry)
                {
                    doc.PseudoPageGeometry.Add(selector);
                }
            }

            foreach (Match box in MarginBoxPattern.Matches(body))
            {
                var request = new MarginBoxRequest
                {
                    Box = box.Groups["box"].Value.ToLowerInvariant(),
                    PageSelector = selector
                };
                foreach (Match d in DeclarationPattern.Matches(box.Groups["body"].Value))
                {
                    var value = d.Groups["value"].Value.Trim();
                    switch (d.Groups["prop"].Value.ToLowerInvariant())
                    {
                        case "content": request.Content = value; break;
                        case "font-family": request.FontFamily = Unquote(value.Split(',')[0].Trim()); break;
                        case "font-size": request.FontSize = ParseLengthPt(value, 9); break;
                        case "color": request.Color = value; break;
                    }
                }
                if (request.Content.Length > 0) doc.MarginBoxes.Add(request);
            }

            ExtractFootnoteArea(body, doc);

            // `counter-reset: footnote` on @page asks for per-page numbering. Recorded
            // here so the planner can say plainly that it is not honoured, rather than
            // renumbering silently and wrongly.
            if (PerPageFootnoteCounterPattern.IsMatch(FootnoteAreaPattern.Replace(body, string.Empty)))
                doc.RequestsPerPageFootnoteNumbering = true;

            // Keep everything except the margin boxes, so `size`/`margin` still reach
            // Chromium — it genuinely implements those and we must not strip them.
            result.Append(css, cursor, start.Index - cursor);
            result.Append(start.Value);
            result.Append(FootnoteAreaPattern.Replace(MarginBoxPattern.Replace(body, string.Empty), string.Empty));
            result.Append('}');
            cursor = end + 1;
        }

        result.Append(css, cursor, css.Length - cursor);
        return result.ToString();
    }

    private static readonly Regex PageSizePattern = new(
        @"^\s*(?<a>[\w.]+(?:mm|cm|in|pt|px)?)(?:\s+(?<b>[\w.]+(?:mm|cm|in|pt|px)?))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// Reads one <c>@page &lt;name&gt;</c> block's page geometry. Only `size` and the
    /// margins are taken: those are what force a separate render, and anything else an
    /// author writes there is ordinary CSS that reaches Chromium unchanged anyway.
    /// </summary>
    private static void ExtractNamedPage(string name, string body, GcpmDocument doc)
    {
        if (!doc.NamedPages.TryGetValue(name, out var definition))
        {
            definition = new NamedPageDefinition { Name = name };
            doc.NamedPages[name] = definition;
        }

        ReadPageGeometry(body, definition);
    }

    /// <summary>Reads the size and margins out of one `@page` block's body.</summary>
    private static void ReadPageGeometry(string body, NamedPageDefinition definition)
    {
        // Nested at-rules (margin boxes, @footnote) carry their own declarations; strip
        // them so a `@top-center { font-size }` is not read as a page margin.
        var flat = FootnoteAreaPattern.Replace(MarginBoxPattern.Replace(body, string.Empty), string.Empty);

        foreach (Match d in DeclarationPattern.Matches(flat))
        {
            var value = d.Groups["value"].Value.Trim();
            switch (d.Groups["prop"].Value.ToLowerInvariant())
            {
                case "size": ApplyPageSize(definition, value); break;
                case "margin": ApplyMarginShorthand(definition, value); break;
                case "margin-top": definition.MarginTop = value; break;
                case "margin-right": definition.MarginRight = value; break;
                case "margin-bottom": definition.MarginBottom = value; break;
                case "margin-left": definition.MarginLeft = value; break;
            }
        }
    }

    private static void ApplyPageSize(NamedPageDefinition definition, string value)
    {
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("landscape", StringComparison.OrdinalIgnoreCase)) definition.Landscape = true;
            else if (token.Equals("portrait", StringComparison.OrdinalIgnoreCase)) definition.Landscape = false;
        }

        // Whatever is left is either a named paper size or an explicit width/height pair.
        var remainder = string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("landscape", StringComparison.OrdinalIgnoreCase)
                     && !t.Equals("portrait", StringComparison.OrdinalIgnoreCase)));
        if (remainder.Length == 0) return;

        var m = PageSizePattern.Match(remainder);
        if (!m.Success) return;

        if (m.Groups["b"].Success && m.Groups["b"].Value.Length > 0)
        {
            definition.Width = m.Groups["a"].Value;
            definition.Height = m.Groups["b"].Value;
        }
        else
        {
            definition.PageSize = m.Groups["a"].Value;
        }
    }

    /// <summary>Expands the 1-to-4 value `margin` shorthand the way CSS does.</summary>
    private static void ApplyMarginShorthand(NamedPageDefinition definition, string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 1:
                definition.MarginTop = definition.MarginRight =
                    definition.MarginBottom = definition.MarginLeft = parts[0];
                break;
            case 2:
                definition.MarginTop = definition.MarginBottom = parts[0];
                definition.MarginRight = definition.MarginLeft = parts[1];
                break;
            case 3:
                definition.MarginTop = parts[0];
                definition.MarginRight = definition.MarginLeft = parts[1];
                definition.MarginBottom = parts[2];
                break;
            case >= 4:
                definition.MarginTop = parts[0];
                definition.MarginRight = parts[1];
                definition.MarginBottom = parts[2];
                definition.MarginLeft = parts[3];
                break;
        }
    }

    /// <summary>
    /// Reads <c>@page { @footnote { ... } }</c> — the rule separating the footnote area
    /// from the body text, plus the area's own type. Only the properties the stamping
    /// pass can actually honour are read; anything else is ignored rather than
    /// half-applied.
    /// </summary>
    private static void ExtractFootnoteArea(string pageBody, GcpmDocument doc)
    {
        foreach (Match area in FootnoteAreaPattern.Matches(pageBody))
        {
            var request = doc.FootnoteArea ??= new FootnoteAreaRequest();
            foreach (Match d in DeclarationPattern.Matches(area.Groups["body"].Value))
            {
                var value = d.Groups["value"].Value.Trim();
                switch (d.Groups["prop"].Value.ToLowerInvariant())
                {
                    case "border-top":
                        // `none`/`0` is the documented way to suppress the separator.
                        if (value.StartsWith("none", StringComparison.OrdinalIgnoreCase))
                        {
                            request.SeparatorEnabled = false;
                        }
                        else
                        {
                            request.SeparatorEnabled = true;
                            request.SeparatorThicknessPt = ParseLengthPt(value, 0.5);
                            var colour = Regex.Match(value, @"#[0-9a-f]{3,6}", RegexOptions.IgnoreCase);
                            if (colour.Success) request.SeparatorColor = colour.Value;
                        }
                        break;
                    case "width":
                        // A percentage is relative to the content width; an absolute
                        // length is taken as-is.
                        if (value.EndsWith("%", StringComparison.Ordinal)
                            && double.TryParse(value.TrimEnd('%'), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out var pct))
                            request.SeparatorWidthFraction = Math.Clamp(pct / 100.0, 0.02, 1.0);
                        else
                            request.SeparatorWidthPt = ParseLengthPt(value, 0);
                        break;
                    case "margin-top": request.SpaceAbovePt = ParseLengthPt(value, 8); break;
                    case "margin-bottom": request.SpaceBelowPt = ParseLengthPt(value, 4); break;
                    case "font-size": request.FontSizePt = ParseLengthPt(value, 9); break;
                    case "font-family": request.FontFamily = Unquote(value.Split(',')[0].Trim()); break;
                    case "color": request.Color = value; break;
                }
            }
        }
    }

    private static int FindMatchingBrace(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Normalises a captured selector: takes the last selector on a comma list, strips a
    /// trailing <c>::before</c>/<c>::after</c> (reporting which), and drops anything that
    /// still looks like leftover declaration text.
    /// </summary>
    private static string CleanSelector(string raw, out bool before)
    {
        before = false;
        var s = raw.Trim();

        // A capture can begin mid-stylesheet, so keep only the text after the last '}'.
        var brace = s.LastIndexOf('}');
        if (brace >= 0) s = s[(brace + 1)..].Trim();
        if (s.Contains(';')) s = s[(s.LastIndexOf(';') + 1)..].Trim();

        // Trim by suffix LENGTH. Searching backwards for ':' is wrong and was measured
        // wrong: for ".toc a::after" it found the second colon and produced ".toc a:",
        // which is an invalid selector and threw inside querySelectorAll.
        foreach (var suffix in new[] { "::before", ":before" })
        {
            if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                before = true;
                s = s[..^suffix.Length].TrimEnd();
                break;
            }
        }
        if (!before)
        {
            foreach (var suffix in new[] { "::after", ":after" })
            {
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s[..^suffix.Length].TrimEnd();
                    break;
                }
            }
        }

        return s.Contains('{') || s.Length == 0 ? string.Empty : s;
    }

    private static string Unquote(string v)
    {
        v = v.Trim();
        return v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0] ? v[1..^1] : v;
    }

    private static double ParseLengthPt(string value, double fallback)
    {
        var m = Regex.Match(value, @"([\d.]+)\s*(pt|px|mm|em)?", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var n)) return fallback;
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "px" => n * 0.75,
            "mm" => n * 72.0 / 25.4,
            "em" => n * 12,
            _ => n
        };
    }
}
