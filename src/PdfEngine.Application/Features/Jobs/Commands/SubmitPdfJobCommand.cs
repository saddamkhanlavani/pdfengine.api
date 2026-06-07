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
        var job = new PdfJob
        {
            TenantId = request.Client.Id,
            ApiKey = request.ApiKey.KeyPrefix,
            EncryptedHtmlContent = _encryptionService.Encrypt(request.Request.HtmlContent),
            DocumentName = request.Request.DocumentName,
            Status = PdfJobStatus.Queued,
            Environment = request.ApiKey.Environment,
            CorrelationId = request.Request.CorrelationId ?? Guid.NewGuid().ToString(),
            SourceType = request.Request.SourceType ?? "API",
            SdkLanguage = request.Request.SdkLanguage,
            SdkVersion = request.Request.SdkVersion,
            RendererVersion = "1.0.0"
        };

        await _storage.SaveJobAsync(job);
        await _queue.EnqueueAsync(job, cancellationToken);

        return Result<string>.Success(job.JobId);
    }
}
