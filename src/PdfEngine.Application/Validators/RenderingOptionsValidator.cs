using System;
using System.Text.RegularExpressions;
using FluentValidation;
using PdfEngine.Application.DTOs;

namespace PdfEngine.Application.Validators;

/// <summary>
/// Shared RenderingOptions rules, used by both the synchronous (GeneratePdfCommand)
/// and asynchronous (SubmitPdfJobCommand) submission paths so they can't drift out of
/// sync the way html/url validation just did — that gap let a malformed async batch
/// item queue successfully and only fail later, inside the worker, instead of
/// immediately with a clear 400 at submission time.
/// </summary>
public class RenderingOptionsValidator : AbstractValidator<RenderingOptions>
{
    private static readonly string[] AllowedPageSizes = { "A4", "Letter", "Legal", "A3", "A5", "A6", "Tabloid", "Ledger" };
    private static readonly string[] AllowedPdfaLevels = { "PDF/A-2b", "PDF/A-3b" };
    private static readonly Regex CssSizeRegex = new(@"^(0|(\d+(\.\d+)?(px|in|cm|mm|pt|pc|em|rem|vh|vw|%)))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PageRangesRegex = new(@"^\d+(-\d+)?(,\s*\d+(-\d+)?)*$", RegexOptions.Compiled);

    public RenderingOptionsValidator()
    {
        RuleFor(x => x.PageSize)
            .Must(size => Array.Exists(AllowedPageSizes, s => s.Equals(size, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("PageSize must be one of: A4, Letter, Legal, A3, A5, A6, Tabloid, Ledger.");

        RuleFor(x => x.MarginTop).Matches(CssSizeRegex).WithMessage("MarginTop must be a valid CSS size.");
        RuleFor(x => x.MarginBottom).Matches(CssSizeRegex).WithMessage("MarginBottom must be a valid CSS size.");
        RuleFor(x => x.MarginLeft).Matches(CssSizeRegex).WithMessage("MarginLeft must be a valid CSS size.");
        RuleFor(x => x.MarginRight).Matches(CssSizeRegex).WithMessage("MarginRight must be a valid CSS size.");

        RuleFor(x => x.Scale).InclusiveBetween(0.1, 2.0).WithMessage("Scale must be between 0.1 and 2.0.");

        RuleFor(x => x.PageRanges)
            .Matches(PageRangesRegex)
            .When(x => !string.IsNullOrWhiteSpace(x.PageRanges))
            .WithMessage("PageRanges must look like '1-5' or '1,3,5-7'.");

        // The regex accepts the SHAPE and says nothing about the numbers, so "9999999-1"
        // and a 200-digit page number both passed it and went to Chromium, which answered
        // with a protocol error the caller saw as HTTP 500 — the caller's malformed input
        // reported as the server's fault. Found by tests/fuzz_gate.py, seed 20260820.
        RuleFor(x => x.PageRanges)
            .Must(HasSanePageNumbers)
            .When(x => !string.IsNullOrWhiteSpace(x.PageRanges) && PageRangesRegex.IsMatch(x.PageRanges!))
            .WithMessage("PageRanges must use page numbers between 1 and 1,000,000, and each range must start at or before it ends (so '5-3' is not a range).");

        RuleFor(x => x.RenderDelayMs)
            .InclusiveBetween(0, 10000)
            .WithMessage("RenderDelayMs must be between 0 and 10000ms — this is an extra settle delay for charts/animations, not a substitute for plan render-time limits.");

        RuleFor(x => x.PdfaCompliance)
            .Must(level => Array.Exists(AllowedPdfaLevels, s => s.Equals(level, StringComparison.OrdinalIgnoreCase)))
            .When(x => !string.IsNullOrWhiteSpace(x.PdfaCompliance))
            .WithMessage("PdfaCompliance must be 'PDF/A-2b' or 'PDF/A-3b'. PDF/A-1b is not offered: its no-transparency rule is routinely violated by ordinary CSS (shadows, gradients, opacity).");

        // The PDF/A spec forbids encryption outright — this is a hard incompatibility,
        // not something to silently resolve one way or the other the way the
        // outline/metadata-under-encryption cases are handled.
        RuleFor(x => x)
            .Must(x => string.IsNullOrWhiteSpace(x.PdfaCompliance) || (string.IsNullOrEmpty(x.OwnerPassword) && string.IsNullOrEmpty(x.UserPassword)))
            .WithMessage("PdfaCompliance and encryption (OwnerPassword/UserPassword) cannot be requested together — the PDF/A specification does not permit encrypted archival documents.");

        // PDF/A-3 is the level that permits arbitrary embedded files; PDF/A-2 permits only
        // embedded PDF/A documents. Silently downgrading or silently dropping the
        // attachment would both produce a file that looks right and fails validation at the
        // recipient — which for an e-invoice means a rejected invoice, not a cosmetic bug.
        RuleFor(x => x)
            .Must(x => x.Attachments is not { Count: > 0 }
                       || string.IsNullOrWhiteSpace(x.PdfaCompliance)
                       || x.PdfaCompliance.Trim().Equals("PDF/A-3b", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Attachments require PDF/A-3b when a PDF/A level is requested. PDF/A-2b permits only embedded PDF/A documents, so an attachment plus 'PDF/A-2b' cannot validate — use 'PDF/A-3b' (which is what Factur-X and ZUGFeRD require anyway) or drop the PDF/A level.");

        // Signing seals a byte range; encrypting afterwards rewrites those bytes, and the
        // signature then fails to verify in every reader. A document that says it is signed
        // and is not is worse than one that was never signed.
        RuleFor(x => x)
            .Must(x => string.IsNullOrWhiteSpace(x.SigningCertificateBase64)
                       || (string.IsNullOrEmpty(x.OwnerPassword) && string.IsNullOrEmpty(x.UserPassword)))
            .WithMessage("Signing and encryption cannot be requested together — signing seals the file's bytes and encryption rewrites them, so the signature would not verify. Sign the document, or encrypt it, not both.");

        // Signing rebuilds the document through PDFsharp to lay out the signature, and that
        // rewrite undoes the object ordering linearization just created — measured, the
        // result came back signed and NOT linearized, with nothing to indicate it. Refused
        // rather than silently returning one of the two things that were asked for.
        RuleFor(x => x)
            .Must(x => !x.Linearize || string.IsNullOrWhiteSpace(x.SigningCertificateBase64))
            .WithMessage("Linearization and signing cannot be requested together — applying the signature rewrites the document and undoes the fast-web-view layout, so the result would be signed but not linearized. Choose one.");

        RuleFor(x => x.SigningCertificatePassword)
            .NotNull()
            .When(x => !string.IsNullOrWhiteSpace(x.SigningCertificateBase64))
            .WithMessage("SigningCertificatePassword is required with a signing certificate (use an empty string if the PKCS#12 bundle has no password).");

        // Forms use NeedAppearances so each reader draws its own controls; PDF/A requires
        // the opposite — baked appearance streams and embedded fonts. Producing the file
        // anyway would hand the caller something that fails their archival validator.
        RuleFor(x => x)
            .Must(x => x.FormFields is not { Count: > 0 } || string.IsNullOrWhiteSpace(x.PdfaCompliance))
            .WithMessage("Interactive form fields and PDF/A cannot be requested together — archival conformance requires baked appearance streams and embedded fonts, while fillable fields are drawn by the reader. Choose a fillable form or an archival document.");

        RuleForEach(x => x.FormFields).ChildRules(f =>
        {
            f.RuleFor(x => x.Name).NotEmpty()
                .WithMessage("Each form field needs a Name — it is the key its value comes back under.");
            f.RuleFor(x => x.Type)
                .Must(t => (t ?? "text").Trim().ToLowerInvariant() is "text" or "checkbox")
                .WithMessage("Form field Type must be 'text' or 'checkbox'.");
            f.RuleFor(x => x.Page).GreaterThan(0)
                .WithMessage("Form field Page is 1-based.");
        });

        RuleForEach(x => x.Attachments).ChildRules(a =>
        {
            a.RuleFor(f => f.FileName)
                .NotEmpty().WithMessage("Each attachment needs a FileName — it is what the reader's attachment pane shows.")
                .Must(n => string.IsNullOrEmpty(n) || n.IndexOfAny(new[] { '/', '\\' }) < 0)
                .WithMessage("An attachment FileName must not contain a path separator.");
            a.RuleFor(f => f.ContentBase64)
                .NotEmpty().WithMessage("Each attachment needs ContentBase64 — the file's bytes, base64-encoded.");
        });
    }

    /// <summary>
    /// Every page number is within a range a document could plausibly have, and every
    /// span runs forwards. Parsed rather than pattern-matched because the failure being
    /// prevented is arithmetic, not syntactic.
    /// </summary>
    private static bool HasSanePageNumbers(string? ranges)
    {
        const int MaxPage = 1_000_000;
        foreach (var part in ranges!.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var span = part.Trim();
            var dash = span.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(span, out var single) || single < 1 || single > MaxPage) return false;
                continue;
            }
            if (!int.TryParse(span[..dash], out var start) ||
                !int.TryParse(span[(dash + 1)..], out var end)) return false;
            if (start < 1 || end < 1 || start > MaxPage || end > MaxPage || start > end) return false;
        }
        return true;
    }

}
