using System.Collections.Generic;
using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface ITypographyEngine : IRenderingStage
{
    /// <summary>
    /// Pass 2: Injects runtime interception routes into the browser page context to intercept remote Google Fonts requests.
    /// </summary>
    Task InterceptFontRequestsAsync(object pageObject);

    /// <summary>
    /// Post-Render: Inspects compiled PDF bytes to check which font families were successfully embedded.
    /// </summary>
    void VerifyEmbeddedFonts(byte[] pdfBytes, List<string> embeddedFonts);
}
