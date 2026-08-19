using MediatR;
using PdfEngine.Application.Common;

namespace PdfEngine.Application.Features.Pdf.Commands;

/// <summary>
/// Mechanical page operations on an already-rendered PDF (T2-4).
///
/// Distinct from <see cref="GeneratePdfCommand"/>, which renders HTML: this takes PDF bytes
/// and rearranges them. Merge already existed; these are the other four operations document
/// assembly needs.
/// </summary>
public class TransformPdfCommand : IRequest<Result<byte[]>>
{
    public string DocumentName { get; set; } = string.Empty;

    /// <summary>The source PDF, base64-encoded.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>extract | rotate | nup | flatten</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Which pages the operation applies to, e.g. "1-3,7,9-". Empty means every page.
    /// For <c>extract</c> this is the selection; for <c>rotate</c> it narrows the rotation.
    /// </summary>
    public string? Pages { get; set; }

    /// <summary>Clockwise rotation for <c>rotate</c>: 90, 180 or 270.</summary>
    public int Rotation { get; set; } = 90;

    /// <summary>Source pages placed on each output sheet for <c>nup</c>: 2, 4, 6, 8, 9 or 16.</summary>
    public int PagesPerSheet { get; set; } = 2;
}
