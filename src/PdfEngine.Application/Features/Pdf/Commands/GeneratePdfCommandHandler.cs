using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class GeneratePdfCommandHandler : IRequestHandler<GeneratePdfCommand, Result<byte[]>>
{
    private readonly IPdfService _pdfService;
    private readonly IPdfJobStorage _jobStorage;
    private readonly IEncryptionService _encryptionService;

    public GeneratePdfCommandHandler(
        IPdfService pdfService, 
        IPdfJobStorage jobStorage, 
        IEncryptionService encryptionService)
    {
        _pdfService = pdfService;
        _jobStorage = jobStorage;
        _encryptionService = encryptionService;
    }

    public async Task<Result<byte[]>> Handle(GeneratePdfCommand request, CancellationToken cancellationToken)
    {
        if (request.TemplateData.HasValue && !string.IsNullOrEmpty(request.HtmlContent))
        {
            var (success, rendered, error) = HtmlTemplateRenderer.Render(request.HtmlContent, request.TemplateData);
            if (!success)
            {
                return Result<byte[]>.Fail(Error.Validation(error ?? "Template rendering failed."));
            }
            request.HtmlContent = rendered;
        }

        var job = new PdfJob
        {
            JobId = request.JobId,
            TenantId = request.Client.Id,
            ApiKey = request.ApiKey?.KeyPrefix ?? "pk_live_default",
            // Url-mode requests have no HTML body — preserve the Url itself in the
            // encrypted field instead so the job record still captures what was
            // actually rendered, for audit/debugging.
            EncryptedHtmlContent = _encryptionService.Encrypt(!string.IsNullOrEmpty(request.HtmlContent) ? request.HtmlContent : (request.Url ?? string.Empty)),
            DocumentName = request.DocumentName,
            Status = PdfJobStatus.Processing,
            Environment = request.ApiKey?.Environment ?? "Production",
            CorrelationId = Guid.NewGuid().ToString(),
            SourceType = "API",
            RendererVersion = "1.0.0",
            CreatedAt = DateTime.UtcNow
        };

        var snapshot = new PdfJobSnapshot
        {
            JobId = request.JobId,
            Html = request.HtmlContent,
            PayloadJson = null,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(request.Options),
            Environment = job.Environment,
            TemplateVersion = job.RendererVersion,
            BrowserVersion = "Chromium",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            await _jobStorage.SaveSnapshotAsync(snapshot);
            await _jobStorage.SaveJobAsync(job);
        }
        catch (Exception ex)
        {
            // Fail silent or log, to prevent blocking generation if db is under heavy load
        }

        var result = await _pdfService.GenerateAsync(request, cancellationToken);

        try
        {
            if (result.IsSuccess)
            {
                job.Status = PdfJobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                job.DiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(request.Diagnostics);
            }
            else
            {
                job.Status = PdfJobStatus.Failed;
                job.ErrorMessage = result.Error.Message;
                job.CompletedAt = DateTime.UtcNow;
                job.DiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(request.Diagnostics);
            }

            await _jobStorage.UpdateJobAsync(job);
        }
        catch (Exception ex)
        {
            // Fail silent or log
        }

        return result;
    }
}

