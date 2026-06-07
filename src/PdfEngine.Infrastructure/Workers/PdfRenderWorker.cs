using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Infrastructure.Workers;

public class PdfRenderWorker : BackgroundService
{
    private readonly ILogger<PdfRenderWorker> _logger;
    private readonly IPdfJobQueue _queue;
    private readonly IPdfJobStorage _jobStorage;
    private readonly IPdfStorage _pdfStorage;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEncryptionService _encryptionService;
    private readonly IWebhookService _webhookService;

    public PdfRenderWorker(
        ILogger<PdfRenderWorker> logger,
        IPdfJobQueue queue,
        IPdfJobStorage jobStorage,
        IPdfStorage pdfStorage,
        IServiceProvider serviceProvider,
        IEncryptionService encryptionService,
        IWebhookService webhookService)
    {
        _logger = logger;
        _queue = queue;
        _jobStorage = jobStorage;
        _pdfStorage = pdfStorage;
        _serviceProvider = serviceProvider;
        _encryptionService = encryptionService;
        _webhookService = webhookService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PdfRenderWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            PdfJob job;
            try
            {
                job = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // 1. Mark as processing
                job.Status = PdfJobStatus.Processing;
                await _jobStorage.UpdateJobAsync(job);

                _logger.LogInformation("Worker processing Job {JobId} for TenantId {TenantId}", job.JobId, job.TenantId);

                // 2. We need a Scope to resolve database and mediator
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
                
                // Load tenant explicitly
                var tenant = await dbContext.Tenants.FindAsync(job.TenantId);
                if (tenant == null)
                {
                    throw new Exception($"Tenant {job.TenantId} not found");
                }

                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                var htmlContent = _encryptionService.Decrypt(job.EncryptedHtmlContent);

                var command = new GeneratePdfCommand
                {
                    Client = tenant,
                    ApiKey = new ApiKey { KeyPrefix = job.ApiKey, Environment = job.Environment },
                    DocumentName = job.DocumentName,
                    HtmlContent = htmlContent
                };

                // 3. Execute render
                var result = await mediator.Send(command, stoppingToken);

                if (result.IsSuccess)
                {
                    var fileUrl = await _pdfStorage.SaveAsync(result.Value!, job.JobId, job.DocumentName);
                    
                    job.Status = PdfJobStatus.Completed;
                    job.FileUrl = fileUrl;
                    job.CompletedAt = DateTime.UtcNow;

                    await _webhookService.DispatchAsync(job.TenantId, "pdf.completed", new
                    {
                        jobId = job.JobId,
                        documentName = job.DocumentName,
                        fileUrl = fileUrl,
                        completedAt = job.CompletedAt,
                        correlationId = job.CorrelationId
                    });
                }
                else
                {
                    job.Status = PdfJobStatus.Failed;
                    job.ErrorMessage = result.Error.Message;
                    job.CompletedAt = DateTime.UtcNow;

                    await _webhookService.DispatchAsync(job.TenantId, "pdf.failed", new
                    {
                        jobId = job.JobId,
                        documentName = job.DocumentName,
                        error = job.ErrorMessage,
                        completedAt = job.CompletedAt,
                        correlationId = job.CorrelationId
                    });
                }

                await _jobStorage.UpdateJobAsync(job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker failed critically on Job {JobId}", job?.JobId);
                if (job != null)
                {
                    job.Status = PdfJobStatus.Failed;
                    job.ErrorMessage = ex.Message;
                    job.CompletedAt = DateTime.UtcNow;
                    await _jobStorage.UpdateJobAsync(job);

                    await _webhookService.DispatchAsync(job.TenantId, "pdf.failed", new
                    {
                        jobId = job.JobId,
                        documentName = job.DocumentName,
                        error = ex.Message,
                        completedAt = job.CompletedAt,
                        correlationId = job.CorrelationId
                    });
                }
            }
        }

        _logger.LogInformation("PdfRenderWorker shutting down.");
    }
}
