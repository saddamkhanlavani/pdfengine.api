using PdfEngine.Application.Common;
using Xunit;

namespace PdfEngine.UnitTests;

public class PdfJobContentEncoderTests
{
    [Fact]
    public void Encode_HtmlOnly_RoundTrips()
    {
        var encoded = PdfJobContentEncoder.Encode("<html><body>Hi</body></html>", null);
        var (html, url) = PdfJobContentEncoder.Decode(encoded);

        Assert.Equal("<html><body>Hi</body></html>", html);
        Assert.Null(url);
    }

    [Fact]
    public void Encode_UrlOnly_RoundTrips()
    {
        var encoded = PdfJobContentEncoder.Encode(null, "https://example.com/invoice/42");
        var (html, url) = PdfJobContentEncoder.Decode(encoded);

        Assert.Null(html);
        Assert.Equal("https://example.com/invoice/42", url);
    }

    [Fact]
    public void Encode_UrlTakesPrecedenceWhenBothSomehowSet()
    {
        var encoded = PdfJobContentEncoder.Encode("<html></html>", "https://example.com");
        var (html, url) = PdfJobContentEncoder.Decode(encoded);

        Assert.Null(html);
        Assert.Equal("https://example.com", url);
    }

    [Fact]
    public void Decode_HtmlThatHappensToContainTheWordUrl_IsNotMisidentified()
    {
        // Guards against a naive substring-based marker instead of a strict prefix
        // check — HTML mentioning "PDFENGINE_URL" in its own text must still decode
        // as HTML, not be misread as a stored URL.
        var html = "<html><body>Contains the text PDFENGINE_URL but is not one.</body></html>";
        var encoded = PdfJobContentEncoder.Encode(html, null);
        var (decodedHtml, decodedUrl) = PdfJobContentEncoder.Decode(encoded);

        Assert.Equal(html, decodedHtml);
        Assert.Null(decodedUrl);
    }
}
