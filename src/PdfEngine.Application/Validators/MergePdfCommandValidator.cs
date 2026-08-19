using FluentValidation;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.Validators;

public class MergePdfCommandValidator : AbstractValidator<MergePdfCommand>
{
    private const int MaxFiles = 50;

    public MergePdfCommandValidator()
    {
        RuleFor(x => x.DocumentName)
            .NotEmpty().WithMessage("Document name is required.");

        RuleFor(x => x.Files)
            .Must(f => f != null && f.Count >= 2).WithMessage("Provide at least two PDF files to merge.")
            .Must(f => f == null || f.Count <= MaxFiles).WithMessage($"Batch size exceeds the maximum of {MaxFiles} files per merge call.");
    }
}
