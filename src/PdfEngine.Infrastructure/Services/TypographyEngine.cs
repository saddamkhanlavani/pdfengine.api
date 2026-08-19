using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class TypographyEngine : ITypographyEngine
{
    private static readonly Dictionary<string, string> FontMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Cinzel", "Cinzel-Regular.ttf" },
        { "Inter", "Inter-Regular.ttf" },
        { "Montserrat", "Montserrat-Regular.ttf" },
        { "Outfit", "Outfit-Regular.ttf" },
        { "Space Grotesk", "SpaceGrotesk-Regular.ttf" },
        { "SpaceGrotesk", "SpaceGrotesk-Regular.ttf" },
        { "Playfair Display", "PlayfairDisplay-Regular.ttf" },
        { "PlayfairDisplay", "PlayfairDisplay-Regular.ttf" },
        { "Roboto Mono", "RobotoMono-Regular.ttf" },
        { "RobotoMono", "RobotoMono-Regular.ttf" },
        { "Roboto", "Inter-Regular.ttf" },
        { "Open Sans", "Inter-Regular.ttf" },
        { "Lato", "Inter-Regular.ttf" },
        { "Poppins", "Inter-Regular.ttf" },
        { "Noto Sans Arabic", "NotoSansArabic-Regular.ttf" },
        { "Noto Sans Devanagari", "NotoSansDevanagari-Regular.ttf" },
        { "Noto Sans JP", "NotoSansJP-Regular.ttf" },
        { "Noto Sans KR", "NotoSansKR-Regular.ttf" },
        { "Noto Sans Hebrew", "NotoSansHebrew-Regular.ttf" },
        { "NotoSansArabic", "NotoSansArabic-Regular.ttf" },
        { "NotoSansDevanagari", "NotoSansDevanagari-Regular.ttf" },
        { "NotoSansJP", "NotoSansJP-Regular.ttf" },
        { "NotoSansKR", "NotoSansKR-Regular.ttf" },
        { "NotoSansHebrew", "NotoSansHebrew-Regular.ttf" },
        { "Noto Sans SC", "NotoSansSC-Regular.ttf" },
        { "Noto Sans TC", "NotoSansTC-Regular.ttf" },
        { "Noto Sans Thai", "NotoSansThai-Regular.ttf" },
        { "Noto Sans Tamil", "NotoSansTamil-Regular.ttf" },
        { "Noto Sans Telugu", "NotoSansTelugu-Regular.ttf" },
        { "Noto Sans Kannada", "NotoSansKannada-Regular.ttf" },
        { "Noto Sans Gujarati", "NotoSansGujarati-Regular.ttf" },
        { "Noto Sans Bengali", "NotoSansBengali-Regular.ttf" },
        { "Noto Sans Gurmukhi", "NotoSansGurmukhi-Regular.ttf" },
        { "Noto Sans Vietnamese", "NotoSansVietnamese-Regular.ttf" },
        { "NotoColorEmoji", "NotoColorEmoji.ttf" },
        { "Noto Color Emoji", "NotoColorEmoji.ttf" },
        { "NotoSansSC", "NotoSansSC-Regular.ttf" },
        { "NotoSansTC", "NotoSansTC-Regular.ttf" },
        { "NotoSansThai", "NotoSansThai-Regular.ttf" },
        { "NotoSansTamil", "NotoSansTamil-Regular.ttf" },
        { "NotoSansTelugu", "NotoSansTelugu-Regular.ttf" },
        { "NotoSansKannada", "NotoSansKannada-Regular.ttf" },
        { "NotoSansGujarati", "NotoSansGujarati-Regular.ttf" },
        { "NotoSansBengali", "NotoSansBengali-Regular.ttf" },
        { "NotoSansGurmukhi", "NotoSansGurmukhi-Regular.ttf" },
        { "NotoSansVietnamese", "NotoSansVietnamese-Regular.ttf" }
    };

    private static readonly ConcurrentDictionary<string, string> FontBase64Cache = new();

    public Task ExecuteAsync(RenderingContext context, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var html = context.Html;
        if (string.IsNullOrEmpty(html))
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Pass 1.1: Force explicit dir="auto" on table cells (td, th) that lack a dir attribute
        html = Regex.Replace(html, @"(<t[dh])\b(?![^>]*\bdir\s*=)([^>]*)>", "$1 dir=\"auto\"$2>", RegexOptions.IgnoreCase);
        context.Html = html;

        // Pass 1: Parse style tags and link references containing googleapis.com/css
        var fontLinkMatches = Regex.Matches(html, @"href=[""']([^""']*fonts\.googleapis\.com/css[^""']*)[""']", RegexOptions.IgnoreCase);
        var fontImportMatches = Regex.Matches(html, @"@import\s+url\([""']?([^""'()]*fonts\.googleapis\.com/css[^""'()]*)[""']?\)", RegexOptions.IgnoreCase);

        var cssBuilder = new StringBuilder();
        
        // Always include bidi cell styling to prevent Webkit/Blink layout reversal in mixed tables
        cssBuilder.AppendLine(@"
            td[dir=""auto""], th[dir=""auto""] {
                unicode-bidi: plaintext !important;
            }
        ");

        foreach (Match match in fontLinkMatches)
        {
            var urlQuery = GetQueryFromUrl(match.Groups[1].Value);
            cssBuilder.Append(GenerateFontCss(urlQuery));
        }

        foreach (Match match in fontImportMatches)
        {
            var urlQuery = GetQueryFromUrl(match.Groups[1].Value);
            cssBuilder.Append(GenerateFontCss(urlQuery));
        }

        if (cssBuilder.Length > 0)
        {
            var styleBlock = $"\n<style>\n{cssBuilder}\n</style>\n";
            var headIdx = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            if (headIdx != -1)
            {
                context.Html = html.Insert(headIdx + 6, styleBlock);
            }
            else
            {
                var htmlIdx = html.IndexOf("<html>", StringComparison.OrdinalIgnoreCase);
                if (htmlIdx != -1)
                {
                    context.Html = html.Insert(htmlIdx + 6, $"\n<head>{styleBlock}</head>\n");
                }
                else
                {
                    context.Html = styleBlock + html;
                }
            }
        }

        return Task.CompletedTask;
    }

    private static string GetQueryFromUrl(string url)
    {
        var idx = url.IndexOf('?');
        return idx != -1 ? url.Substring(idx) : string.Empty;
    }

    public async Task InterceptFontRequestsAsync(object pageObject)
    {
        if (pageObject is not IPage page)
        {
            throw new ArgumentException("Page object must implement Microsoft.Playwright.IPage", nameof(pageObject));
        }

        // Intercept fonts.googleapis.com
        await page.RouteAsync(url => url.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase), async route =>
        {
            try
            {
                var uri = new Uri(route.Request.Url);
                var css = GenerateFontCss(uri.Query);
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/css",
                    Body = css
                });
            }
            catch
            {
                try { await route.AbortAsync(); } catch {}
            }
        });

        // Intercept fonts.gstatic.com
        await page.RouteAsync(url => url.Contains("fonts.gstatic.com", StringComparison.OrdinalIgnoreCase), async route =>
        {
            try
            {
                var uri = new Uri(route.Request.Url);
                var matchedFile = "Inter-Regular.ttf";
                foreach (var kvp in FontMap)
                {
                    if (uri.PathAndQuery.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedFile = kvp.Value;
                        break;
                    }
                }

                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts", matchedFile);
                if (!File.Exists(path))
                {
                    var alternativePath = Path.Combine(Directory.GetCurrentDirectory(), "src", "PdfEngine.Infrastructure", "Fonts", matchedFile);
                    if (File.Exists(alternativePath)) path = alternativePath;
                }

                if (File.Exists(path))
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    await route.FulfillAsync(new RouteFulfillOptions
                    {
                        Status = 200,
                        ContentType = "font/truetype",
                        BodyBytes = bytes
                    });
                }
                else
                {
                    await route.AbortAsync();
                }
            }
            catch
            {
                try { await route.AbortAsync(); } catch {}
            }
        });
    }

    /// <summary>
    /// Confirms which of our custom fonts actually rendered glyphs in the output PDF,
    /// by parsing the real PDF structure with PdfPig and reading the PostScript font
    /// name each letter was drawn with — not a text search on raw PDF bytes (which
    /// cannot reliably find anything inside compressed content streams and previously
    /// made this check silently non-functional). A subsetted font's name appears as
    /// e.g. "ABCDEF+Inter-Regular" in the content stream; matching on the base name
    /// still works because .Contains is used, not an exact match.
    /// Matches against BOTH the source TTF filename and the CSS family name (spaces
    /// stripped) — verified empirically that Chromium/Skia can synthesize a PDF font
    /// name that doesn't match the filename at all (e.g. a `font-weight: 100 900`
    /// @font-face range gets embedded as "MontserratThin-Regular", which contains
    /// "Montserrat" but not the filename's "Montserrat-Regular").
    /// </summary>
    public void VerifyEmbeddedFonts(byte[] pdfBytes, List<string> embeddedFonts)
    {
        if (pdfBytes == null || embeddedFonts == null) return;
        try
        {
            using var document = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var fontNamesUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var page in document.GetPages())
            {
                foreach (var letter in page.Letters)
                {
                    if (!string.IsNullOrEmpty(letter.FontName))
                    {
                        fontNamesUsed.Add(letter.FontName);
                    }
                }
            }

            foreach (var kvp in FontMap)
            {
                var fontBaseName = Path.GetFileNameWithoutExtension(kvp.Value);
                var familyNoSpaces = kvp.Key.Replace(" ", string.Empty);

                var matched = fontNamesUsed.Any(used =>
                    used.Contains(fontBaseName, StringComparison.OrdinalIgnoreCase) ||
                    used.Contains(familyNoSpaces, StringComparison.OrdinalIgnoreCase));

                if (matched && !embeddedFonts.Contains(kvp.Key))
                {
                    embeddedFonts.Add(kvp.Key);
                }
            }
        }
        catch
        {
            // Parsing failure shouldn't fail the render — font verification is a
            // diagnostic, not a rendering-correctness requirement.
        }
    }

    private static string GenerateFontCss(string query)
    {
        var families = new List<string>();
        var matches = Regex.Matches(query, @"family=([^&:]+)");
        foreach (Match match in matches)
        {
            var familyName = Uri.UnescapeDataString(match.Groups[1].Value).Replace("+", " ");
            families.Add(familyName);
        }

        if (families.Count == 0)
        {
            families.Add("Inter");
        }

        var cssBuilder = new StringBuilder();
        foreach (var family in families)
        {
            if (!FontMap.TryGetValue(family, out var fontFile))
            {
                fontFile = "Inter-Regular.ttf";
            }

            var base64 = FontBase64Cache.GetOrAdd(fontFile, file =>
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fonts", file);
                if (!File.Exists(path))
                {
                    var alternativePath = Path.Combine(Directory.GetCurrentDirectory(), "src", "PdfEngine.Infrastructure", "Fonts", file);
                    if (File.Exists(alternativePath)) path = alternativePath;
                }

                if (File.Exists(path))
                {
                    return Convert.ToBase64String(File.ReadAllBytes(path));
                }
                return string.Empty;
            });

            if (!string.IsNullOrEmpty(base64))
            {
                // font-weight is declared as the SINGLE weight this file actually is —
                // not the full 100-900 range it previously claimed. That claim was a real
                // rendering defect: one Regular file was offered as every weight, so
                // Chromium believed a matching face existed and used it verbatim for
                // font-weight:700. Measured before the fix — weights 100, 400 and 900
                // produced byte-identical ink and embedded one face, i.e. bold text was
                // silently not bold. Declaring the true weight lets the browser
                // synthesize the others, which is visibly heavier and honest about being
                // an approximation.
                cssBuilder.AppendLine($@"
                    @font-face {{
                        font-family: '{family}';
                        font-style: normal;
                        font-weight: 400;
                        font-display: swap;
                        src: url(data:font/truetype;charset=utf-8;base64,{base64}) format('truetype');
                    }}
                ");
            }
        }
        return cssBuilder.ToString();
    }
}
