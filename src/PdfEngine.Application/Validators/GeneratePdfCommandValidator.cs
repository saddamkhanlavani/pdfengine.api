using System;
using FluentValidation;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.Validators;

public class GeneratePdfCommandValidator : AbstractValidator<GeneratePdfCommand>
{
    public GeneratePdfCommandValidator()
    {
        RuleFor(x => x.DocumentName)
            .NotEmpty().WithMessage("Document name is required.");

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.HtmlContent) ^ !string.IsNullOrWhiteSpace(x.Url))
            .WithMessage("Provide exactly one of 'html' or 'url' — not both, not neither.");

        RuleFor(x => x.Url)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .When(x => !string.IsNullOrWhiteSpace(x.Url))
            .WithMessage("Url must be an absolute http or https URL.");

        RuleFor(x => x.DocumentType)
            .IsInEnum().WithMessage("DocumentType must be a valid enum value (Invoice, Report, Certificate, Statement, Custom).");

        // Rejected here, before any rendering work, so the caller gets a clear 400 rather
        // than a dead process. Deeply nested markup overflows the HTML parser's stack —
        // measured at ~6,000 nested elements, which terminated the whole API and every
        // other tenant's in-flight render with it. A stack overflow is uncatchable in
        // .NET, so refusing the input is the only available defence.
        RuleFor(x => x.HtmlContent)
            .Must(html => MeasureMaxNestingDepth(html) <= MaxHtmlNestingDepth)
            .When(x => !string.IsNullOrWhiteSpace(x.HtmlContent))
            .WithMessage($"HTML nesting depth exceeds the maximum of {MaxHtmlNestingDepth} "
                       + "levels. Deeply nested markup can exhaust the parser stack.");

        When(x => x.Options != null, () =>
        {
            RuleFor(x => x.Options).SetValidator(new RenderingOptionsValidator());
        });
    }

    internal const int MaxHtmlNestingDepth = 512;

    private static readonly System.Collections.Generic.HashSet<string> VoidElements =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

    private static readonly System.Text.RegularExpressions.Regex TagPattern =
        new(@"<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9:-]*)([^>]*?)(/?)\s*>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Maximum open-element depth of the raw markup. A string scan, not a DOM walk: this
    /// exists to protect the parser, so it must not itself invoke one. Void and
    /// self-closing tags never open a level — counting them would report a long run of
    /// <c>&lt;br&gt;</c> as deep nesting and reject ordinary documents.
    /// </summary>
    internal static int MeasureMaxNestingDepth(string? html)
    {
        if (string.IsNullOrEmpty(html)) return 0;
        int depth = 0, max = 0;
        foreach (System.Text.RegularExpressions.Match m in TagPattern.Matches(html))
        {
            var name = m.Groups[2].Value;
            if (VoidElements.Contains(name)) continue;

            if (m.Groups[1].Value == "/")
            {
                if (depth > 0) depth--;
            }
            else if (m.Groups[4].Value != "/")
            {
                depth++;
                if (depth > max) max = depth;
            }
        }
        return max;
    }
}
