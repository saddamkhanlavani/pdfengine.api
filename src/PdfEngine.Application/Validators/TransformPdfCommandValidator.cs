using System;
using System.Linq;
using FluentValidation;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.Validators;

public class TransformPdfCommandValidator : AbstractValidator<TransformPdfCommand>
{
    private static readonly string[] Operations = { "extract", "rotate", "nup", "flatten" };

    /// <summary>
    /// Sheet layouts the engine can actually arrange. An arbitrary N would need a grid it
    /// has no sensible answer for, and silently rounding to the nearest supported value
    /// would put the caller's pages somewhere they did not ask for.
    /// </summary>
    private static readonly int[] SheetLayouts = { 2, 4, 6, 8, 9, 16 };

    public TransformPdfCommandValidator()
    {
        RuleFor(x => x.DocumentName).NotEmpty().WithMessage("Document name is required.");
        RuleFor(x => x.File).NotEmpty().WithMessage("Provide the source PDF as base64 in 'file'.");

        RuleFor(x => x.Operation)
            .Must(op => Operations.Contains((op ?? string.Empty).Trim().ToLowerInvariant()))
            .WithMessage($"Operation must be one of: {string.Join(", ", Operations)}.");

        RuleFor(x => x.Rotation)
            .Must(r => r is 90 or 180 or 270)
            .When(x => string.Equals(x.Operation?.Trim(), "rotate", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Rotation must be 90, 180 or 270 degrees clockwise.");

        RuleFor(x => x.PagesPerSheet)
            .Must(n => SheetLayouts.Contains(n))
            .When(x => string.Equals(x.Operation?.Trim(), "nup", StringComparison.OrdinalIgnoreCase))
            .WithMessage($"PagesPerSheet must be one of: {string.Join(", ", SheetLayouts)}.");

        RuleFor(x => x.Pages)
            .NotEmpty()
            .When(x => string.Equals(x.Operation?.Trim(), "extract", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Extract needs a page selection, e.g. '1-3,7'. Extracting every page would just copy the document.");
    }
}
