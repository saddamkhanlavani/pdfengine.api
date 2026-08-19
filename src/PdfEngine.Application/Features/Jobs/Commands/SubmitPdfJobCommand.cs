using System.Threading;
using System.Threading.Tasks;
using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.Features.Jobs.Commands;

public class SubmitPdfJobCommand : IRequest<Result<string>>
{
    public Tenant Client { get; set; } = null!;
    public ApiKey ApiKey { get; set; } = null!;
    public GeneratePdfRequest Request { get; set; } = null!;
}

public class SubmitPdfJobCommandHandler : IRequestHandler<SubmitPdfJobCommand, Result<string>>
{
    private readonly IPdfJobQueue _queue;
    private readonly IPdfJobStorage _storage;
    private readonly IEncryptionService _encryptionService;

    public SubmitPdfJobCommandHandler(
        IPdfJobQueue queue, 
        IPdfJobStorage storage, 
        IEncryptionService encryptionService)
    {
        _queue = queue;
        _storage = storage;
        _encryptionService = encryptionService;
    }

    public async Task<Result<string>> Handle(SubmitPdfJobCommand request, CancellationToken cancellationToken)
    {
        // Rendered once, here, before the job is ever stored or queued — the worker
        // that eventually processes this job only ever sees final HTML, the same as
        // any other request. It never needs to know templating exists.
        if (request.Request.TemplateData.HasValue && !string.IsNullOrEmpty(request.Request.HtmlContent))
        {
            var (success, rendered, error) = HtmlTemplateRenderer.Render(request.Request.HtmlContent, request.Request.TemplateData);
            if (!success)
            {
                return Result<string>.Fail(Error.Validation(error ?? "Template rendering failed."));
            }
            request.Request.HtmlContent = rendered;
        }

        var job = new PdfJob
        {
            TenantId = request.Client.Id,
            ApiKey = request.ApiKey.KeyPrefix,
            EncryptedHtmlContent = _encryptionService.Encrypt(
                PdfEngine.Application.Common.PdfJobContentEncoder.Encode(request.Request.HtmlContent, request.Request.Url)),
            DocumentName = request.Request.DocumentName,
            Status = PdfJobStatus.Queued,
            Environment = request.ApiKey.Environment,
            CorrelationId = request.Request.CorrelationId ?? Guid.NewGuid().ToString(),
            SourceType = request.Request.SourceType ?? "API",
            SdkLanguage = request.Request.SdkLanguage,
            SdkVersion = request.Request.SdkVersion,
            RendererVersion = "1.0.0"
        };

        var snapshot = new PdfJobSnapshot
        {
            JobId = job.JobId,
            Html = !string.IsNullOrEmpty(request.Request.HtmlContent) ? request.Request.HtmlContent : (request.Request.Url ?? string.Empty),
            PayloadJson = null,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(request.Request.Options),
            Environment = job.Environment,
            TemplateVersion = job.RendererVersion,
            BrowserVersion = "Chromium"
        };

        await _storage.SaveSnapshotAsync(snapshot);
        await _storage.SaveJobAsync(job);
        await _queue.EnqueueAsync(job, cancellationToken);

        return Result<string>.Success(job.JobId);
    }
}
