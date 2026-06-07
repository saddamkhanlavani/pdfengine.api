using System;
using System.Text.RegularExpressions;
using FluentValidation;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.Validators;

public class GeneratePdfCommandValidator : AbstractValidator<GeneratePdfCommand>
{
    private static readonly string[] AllowedPageSizes = { "A4", "Letter", "Legal" };
    
    // Basic CSS size pattern, e.g., 20px, 1in, 2cm, 0
    private static readonly Regex CssSizeRegex = new(@"^(0|(\d+(\.\d+)?(px|in|cm|mm|pt|pc|em|rem|vh|vw|%)))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public GeneratePdfCommandValidator()
    {
        RuleFor(x => x.DocumentName)
            .NotEmpty().WithMessage("Document name is required.");

        RuleFor(x => x.HtmlContent)
            .NotEmpty().WithMessage("HTML content cannot be empty.");

        When(x => x.Options != null, () =>
        {
            RuleFor(x => x.Options.PageSize)
                .Must(size => Array.Exists(AllowedPageSizes, s => s.Equals(size, StringComparison.OrdinalIgnoreCase)))
                .WithMessage("PageSize must be one of: A4, Letter, Legal.");

            RuleFor(x => x.Options.MarginTop).Matches(CssSizeRegex).WithMessage("MarginTop must be a valid CSS size.");
            RuleFor(x => x.Options.MarginBottom).Matches(CssSizeRegex).WithMessage("MarginBottom must be a valid CSS size.");
            RuleFor(x => x.Options.MarginLeft).Matches(CssSizeRegex).WithMessage("MarginLeft must be a valid CSS size.");
            RuleFor(x => x.Options.MarginRight).Matches(CssSizeRegex).WithMessage("MarginRight must be a valid CSS size.");
        });
    }
}
