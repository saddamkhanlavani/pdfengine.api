using System;
using FluentValidation;
using PdfEngine.Application.Features.Jobs.Commands;

namespace PdfEngine.Application.Validators;

/// <summary>
/// The async job submission path previously had no validator at all — confirmed by
/// testing: a batch item with neither html nor url queued successfully instead of
/// being rejected, and would only have failed later inside the worker. Mirrors
/// GeneratePdfCommandValidator's rules against the nested Request object.
/// </summary>
public class SubmitPdfJobCommandValidator : AbstractValidator<SubmitPdfJobCommand>
{
    public SubmitPdfJobCommandValidator()
    {
        RuleFor(x => x.Request).NotNull().WithMessage("Request body is required.");

        When(x => x.Request != null, () =>
        {
            RuleFor(x => x.Request.DocumentName)
                .NotEmpty().WithMessage("Document name is required.");

            RuleFor(x => x.Request)
                .Must(r => !string.IsNullOrWhiteSpace(r.HtmlContent) ^ !string.IsNullOrWhiteSpace(r.Url))
                .WithMessage("Provide exactly one of 'html' or 'url' — not both, not neither.");

            RuleFor(x => x.Request.Url)
                .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .When(x => !string.IsNullOrWhiteSpace(x.Request.Url))
                .WithMessage("Url must be an absolute http or https URL.");

            RuleFor(x => x.Request.DocumentType)
                .IsInEnum().WithMessage("DocumentType must be a valid enum value (Invoice, Report, Certificate, Statement, Custom).");

            When(x => x.Request.Options != null, () =>
            {
                RuleFor(x => x.Request.Options).SetValidator(new RenderingOptionsValidator());
            });
        });
    }
}
