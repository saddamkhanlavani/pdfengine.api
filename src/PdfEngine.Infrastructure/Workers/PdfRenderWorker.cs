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

    private int _consecutiveDequeueFailures;

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
            catch (Exception ex)
            {
                // The queue is Redis, and Redis restarts. This catch used to cover only
                // cancellation, so a connection failure escaped ExecuteAsync, faulted the
                // BackgroundService, and .NET's default StopHost behaviour shut the entire
                // API down — every tenant's synchronous rendering included, none of which
                // needs the queue. Measured by tests/chaos_gate.py: the container exited
                // with code 0 while a dependency was unreachable.
                //
                // Backing off matters as much as catching: without it this loop spins on a
                // failing connection and floods the log faster than the outage resolves.
                _consecutiveDequeueFailures++;
                var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, _consecutiveDequeueFailures))));
                _logger.LogError(ex,
                    "Could not read from the job queue (failure {Count}); retrying in {Backoff}s. Queued jobs are delayed; synchronous rendering is unaffected.",
                    _consecutiveDequeueFailures, backoff.TotalSeconds);
                try { await Task.Delay(backoff, stoppingToken); } catch (OperationCanceledException) { break; }
                continue;
            }

            _consecutiveDequeueFailures = 0;

            try
            {
                // 1. Mark as processing and calculate queue latency
                job.Status = PdfJobStatus.Processing;
                job.QueueWaitDurationMs = (long)(DateTime.UtcNow - job.CreatedAt).TotalMilliseconds;
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

                var decryptedContent = _encryptionService.Decrypt(job.EncryptedHtmlContent);
                var (htmlContent, jobUrl) = PdfEngine.Application.Common.PdfJobContentEncoder.Decode(decryptedContent);

                var options = new PdfEngine.Application.DTOs.RenderingOptions();
                var snapshot = await dbContext.PdfJobSnapshots.FindAsync(new object[] { job.JobId }, stoppingToken);
                if (snapshot != null && !string.IsNullOrEmpty(snapshot.OptionsJson))
                {
                    try
                    {
                        options = System.Text.Json.JsonSerializer.Deserialize<PdfEngine.Application.DTOs.RenderingOptions>(snapshot.OptionsJson) ?? options;
                    }
                    catch {}
                }

                var command = new GeneratePdfCommand
                {
                    Client = tenant,
                    ApiKey = new ApiKey { KeyPrefix = job.ApiKey, Environment = job.Environment },
                    DocumentName = job.DocumentName,
                    HtmlContent = htmlContent ?? string.Empty,
                    Url = jobUrl,
                    Options = options
                };

                // 3. Execute render
                var result = await mediator.Send(command, stoppingToken);

                try
                {
                    job.DiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(command.Diagnostics);
                }
                catch (Exception diagEx)
                {
                    _logger.LogWarning(diagEx, "Failed to serialize rendering diagnostics for job {JobId}", job.JobId);
                }

                // Save rendering snapshot details
                try
                {
                    if (snapshot == null)
                    {
                        snapshot = new PdfJobSnapshot
                        {
                            JobId = job.JobId,
                            Html = htmlContent,
                            PayloadJson = null,
                            OptionsJson = System.Text.Json.JsonSerializer.Serialize(command.Options),
                            Environment = job.Environment,
                            TemplateVersion = job.RendererVersion,
                            BrowserVersion = "Chromium"
                        };
                        dbContext.PdfJobSnapshots.Add(snapshot);
                    }
                    
                    snapshot.HarJson = command.Diagnostics.HarJson;
                    await dbContext.SaveChangesAsync(stoppingToken);
                }
                catch (Exception snapEx)
                {
                    _logger.LogError(snapEx, "Failed to save or update PdfJobSnapshot for job {JobId}", job.JobId);
                }

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
                    try
                    {
                        job.RetryCount++;
                        if (job.RetryCount >= job.MaxRetries)
                        {
                            job.Status = PdfJobStatus.Failed;
                            job.IsDeadLetter = true;
                            job.ErrorMessage = $"Max retries reached. Last Error: {ex.Message}";
                            job.CompletedAt = DateTime.UtcNow;

                            await _jobStorage.UpdateJobAsync(job);
                            await _queue.EnqueueDeadLetterAsync(job);

                            _logger.LogError("Job {JobId} exceeded max retries ({MaxRetries}) and is sent to Dead Letter Queue (DLQ).", job.JobId, job.MaxRetries);

                            await _webhookService.DispatchAsync(job.TenantId, "pdf.failed", new
                            {
                                jobId = job.JobId,
                                documentName = job.DocumentName,
                                error = job.ErrorMessage,
                                isDeadLetter = true,
                                completedAt = job.CompletedAt,
                                correlationId = job.CorrelationId
                            });
                        }
                        else
                        {
                            job.Status = PdfJobStatus.Queued;
                            job.ErrorMessage = ex.Message;
                            
                            await _jobStorage.UpdateJobAsync(job);
                            await _queue.EnqueueAsync(job, stoppingToken);

                            _logger.LogWarning("Job {JobId} failed (attempt {RetryCount}/{MaxRetries}). Re-queued for retry. Error: {Error}", job.JobId, job.RetryCount, job.MaxRetries, ex.Message);
                        }
                    }
                    catch (Exception dbEx)
                    {
                        _logger.LogError(dbEx, "Failed to update or re-enqueue failed job {JobId}", job.JobId);
                    }
                }
            }
        }

        _logger.LogInformation("PdfRenderWorker shutting down.");
    }
}
