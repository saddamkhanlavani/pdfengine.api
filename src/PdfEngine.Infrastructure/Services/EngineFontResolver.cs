using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharpCore.Fonts;

namespace PdfEngine.Infrastructure.Services;

/// <summary>
/// Supplies font faces to PdfSharpCore for everything the ENGINE draws itself — footnote
/// bands, running headers and footers, watermarks.
///
/// Without one registered, PdfSharpCore falls back to a default resolver that was measured
/// to return the same single face for every family and every style: "Helvetica", "Arial",
/// "Times New Roman" and "Verdana" all measured byte-identical, and so did Regular, Bold
/// and Italic. Two consequences, both silent: a `font-family` asked for in a `@page` margin
/// box did nothing at all, and emphasis inside a footnote rendered upright and regular.
///
/// This resolves against the fonts the engine already bundles, which is also what keeps it
/// portable — the same faces are present on a developer laptop and inside the container,
/// so output does not change with whatever the host happens to have installed.
/// </summary>
internal sealed class EngineFontResolver : IFontResolver
{
    /// <summary>The three families bundled with a complete Regular/Bold/Italic/BoldItalic
    /// set. Everything else can only ever resolve to a Regular.</summary>
    private const string SansFamily = "Carlito";
    private const string SerifFamily = "Caladea";
    private const string MonoFamily = "LiberationMono";

    private static readonly ConcurrentDictionary<string, byte[]> FontData = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Dictionary<string, string>> FilesByKey = new(IndexFontDirectory);

    public string DefaultFontName => SansFamily;

    /// <summary>
    /// Where the bundled fonts live at run time. The project copies `Fonts/**` next to the
    /// assembly, so this is beside the DLL rather than anywhere on the host.
    /// </summary>
    private static string FontDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fonts");

    private static Dictionary<string, string> IndexFontDirectory()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(FontDirectory)) return index;
            foreach (var path in Directory.EnumerateFiles(FontDirectory, "*.ttf"))
            {
                // "Carlito-BoldItalic.ttf" -> key "carlito-bolditalic"
                index[Path.GetFileNameWithoutExtension(path)] = path;
            }
        }
        catch (Exception)
        {
            // An unreadable font directory degrades to PdfSharpCore's own fallback rather
            // than failing every render that draws text.
        }
        return index;
    }

    /// <summary>
    /// Maps a requested family to one the engine actually ships.
    ///
    /// Generic CSS families and the base-14 PostScript names both arrive here — a margin
    /// box asking for "Helvetica" and one asking for "sans-serif" want the same thing — so
    /// they are folded onto the bundled sans, serif or mono face.
    /// </summary>
    private static string NormalizeFamily(string? requested)
    {
        var name = (requested ?? string.Empty).Trim().Trim('\'', '"');
        if (name.Length == 0) return SansFamily;

        var lower = name.ToLowerInvariant();

        if (lower is "serif" or "times" or "times new roman" or "georgia" or "garamond"
            or "cambria" or "book antiqua" or "palatino") return SerifFamily;

        if (lower is "monospace" or "courier" or "courier new" or "consolas" or "menlo"
            or "monaco" or "sf mono" or "ui-monospace") return MonoFamily;

        if (lower is "sans-serif" or "system-ui" or "ui-sans-serif" or "helvetica"
            or "helvetica neue" or "arial" or "segoe ui" or "roboto" or "calibri"
            or "-apple-system") return SansFamily;

        // A family the engine bundles by name (Inter, Outfit, NotoSansArabic, ...) is used
        // as-is; anything else falls back to the sans face rather than failing.
        return FilesByKey.Value.Keys.Any(k => k.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)
                                           || k.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? name
            : SansFamily;
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var family = NormalizeFamily(familyName);
        var files = FilesByKey.Value;

        // Preferred face first, then progressively less specific ones. A family bundled
        // Regular-only still resolves — it just cannot honour the emphasis, which is
        // reported at render time rather than silently substituted.
        var candidates = new List<string>();
        if (isBold && isItalic) candidates.Add($"{family}-BoldItalic");
        if (isBold) candidates.Add($"{family}-Bold");
        if (isItalic) candidates.Add($"{family}-Italic");
        candidates.Add($"{family}-Regular");
        candidates.Add(family);

        // Last resort: the bundled sans, which is always present.
        if (isBold && isItalic) candidates.Add($"{SansFamily}-BoldItalic");
        if (isBold) candidates.Add($"{SansFamily}-Bold");
        if (isItalic) candidates.Add($"{SansFamily}-Italic");
        candidates.Add($"{SansFamily}-Regular");

        foreach (var key in candidates)
        {
            if (files.ContainsKey(key)) return new FontResolverInfo(key);
        }

        return files.Count > 0 ? new FontResolverInfo(files.Keys.First()) : null;
    }

    public byte[]? GetFont(string faceName)
    {
        return FontData.GetOrAdd(faceName, key =>
        {
            try
            {
                return FilesByKey.Value.TryGetValue(key, out var path)
                    ? File.ReadAllBytes(path)
                    : Array.Empty<byte>();
            }
            catch (Exception)
            {
                return Array.Empty<byte>();
            }
        });
    }

    /// <summary>
    /// Whether the bundled files can actually draw emphasis for this family.
    ///
    /// The three default faces ship a full Regular/Bold/Italic/BoldItalic set, but a
    /// document that asks a footnote band for a family bundled Regular-only cannot have its
    /// emphasis honoured — and that is worth reporting rather than silently flattening.
    /// </summary>
    internal static bool SupportsEmphasis(string? family)
    {
        var normalized = NormalizeFamily(family);
        var files = FilesByKey.Value;
        return files.ContainsKey($"{normalized}-Bold") && files.ContainsKey($"{normalized}-Italic");
    }

    /// <summary>
    /// Installs the resolver once for the process. PdfSharpCore keeps it in a static, so
    /// this is idempotent and safe to call from startup.
    /// </summary>
    internal static void Register()
    {
        if (GlobalFontSettings.FontResolver is EngineFontResolver) return;
        GlobalFontSettings.FontResolver = new EngineFontResolver();
    }
}
