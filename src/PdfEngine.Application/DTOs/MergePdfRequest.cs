using System.Collections.Generic;

namespace PdfEngine.Application.DTOs;

/// <summary>
/// Combines multiple existing PDFs (base64-encoded) into a single document, in the
/// order supplied — e.g. a cover letter + a generated invoice + terms-and-conditions
/// as one deliverable. Distinct from GeneratePdfRequest, which renders HTML; this
/// operates on already-rendered PDF bytes.
/// </summary>
public class MergePdfRequest
{
    public string DocumentName { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
}


/// <summary>
/// Mechanical page operations on an already-rendered PDF (T2-4): extract a page range,
/// rotate pages, remove the interactive layer, or place several pages per sheet.
/// </summary>
public class TransformPdfRequest
{
    public string DocumentName { get; set; } = string.Empty;

    /// <summary>The source PDF, base64-encoded.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>extract | rotate | nup | flatten</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Page selection such as "1-3,7,9-". Order is preserved, so "3,1" reorders.</summary>
    public string? Pages { get; set; }

    /// <summary>Clockwise rotation for `rotate`: 90, 180 or 270.</summary>
    public int Rotation { get; set; } = 90;

    /// <summary>Source pages per output sheet for `nup`: 2, 4, 6, 8, 9 or 16.</summary>
    public int PagesPerSheet { get; set; } = 2;
}
