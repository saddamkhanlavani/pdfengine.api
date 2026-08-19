using System.Threading;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.DTOs;

public class RenderingContext
{
    public string Html { get; set; }

    // The document exactly as the caller supplied it, captured before sanitization.
    // Required for GCPM parsing (string-set, target-counter, leader, @page margin boxes):
    // the sanitizer necessarily drops CSS properties and at-rules it does not recognise,
    // and those constructs are precisely the ones it does not recognise — reading Html
    // after sanitization finds nothing. Used for READING authored intent only; no markup
    // from here is ever re-injected into the page.
    public string OriginalHtml { get; set; }
    public string Css { get; set; }
    public DocumentModel Model { get; set; }
    public LayoutModel Layout { get; set; }
    public PaginationPlan Plan { get; set; }
    public GeneratePdfDiagnostics Diagnostics { get; set; }
    public CancellationToken CancellationToken { get; set; }
    public object? Page { get; set; }
    public RenderingOptions? Options { get; set; }

    public RenderingContext(string html, GeneratePdfDiagnostics diagnostics, CancellationToken cancellationToken = default)
    {
        Html = html;
        OriginalHtml = html;
        Css = string.Empty;
        Model = new DocumentModel();
        Layout = new LayoutModel();
        Plan = new PaginationPlan();
        Diagnostics = diagnostics;
        CancellationToken = cancellationToken;
    }
}
