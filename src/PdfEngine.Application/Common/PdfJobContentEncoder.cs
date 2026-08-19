namespace PdfEngine.Application.Common;

/// <summary>
/// PdfJob has a single EncryptedHtmlContent field for the render source — adding a
/// real Url column would need a schema migration. Until that's justified, a Url-mode
/// job's target URL is stored in the same field behind a marker prefix that can never
/// collide with real HTML: a leading null byte, which the HTML5 spec treats as a parse
/// error and strips, so it can never legitimately start real HTML content. Encode/
/// Decode here are the only two places that need to know this.
/// </summary>
public static class PdfJobContentEncoder
{
    private const string UrlMarker = "\0PDFENGINE_URL\0";

    public static string Encode(string? htmlContent, string? url)
    {
        if (!string.IsNullOrEmpty(url)) return UrlMarker + url;
        return htmlContent ?? string.Empty;
    }

    public static (string? HtmlContent, string? Url) Decode(string stored)
    {
        if (stored.StartsWith(UrlMarker, System.StringComparison.Ordinal))
        {
            return (null, stored.Substring(UrlMarker.Length));
        }
        return (stored, null);
    }
}
