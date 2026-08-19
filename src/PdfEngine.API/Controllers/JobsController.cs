using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Features.Jobs.Commands;
using PdfEngine.Application.Features.Jobs.Queries;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;

namespace PdfEngine.API.Controllers;

[ApiController]
[Route("api/v1/pdf/jobs")]
public class JobsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IPdfStorage _pdfStorage;
    private readonly IEncryptionService _encryptionService;

    public JobsController(IMediator mediator, IPdfStorage pdfStorage, IEncryptionService encryptionService)
    {
        _mediator = mediator;
        _pdfStorage = pdfStorage;
        _encryptionService = encryptionService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] GeneratePdfRequest request)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        var apiKey = HttpContext.Items["ApiKey"] as ApiKey;
        if (client == null)
        {
            return Unauthorized("Tenant not found in context.");
        }

        var command = new SubmitPdfJobCommand
        {
            Client = client,
            ApiKey = apiKey ?? new ApiKey { KeyPrefix = "jwt_auth", Environment = "Production" },
            Request = request
        };

        var result = await _mediator.Send(command);

        if (!result.IsSuccess)
        {
            if (result.Error.Code == ErrorCodes.QuotaExceeded)
            {
                return StatusCode(429, new { Error = result.Error.Message });
            }
            return BadRequest(new { Error = result.Error.Message });
        }

        return Accepted(new { JobId = result.Value });
    }

    // Submits many documents in one call instead of one HTTP round-trip per document —
    // each is queued as its own async job (same path as CreateJob), so results arrive
    // via the existing per-job status/download/webhook mechanism, not as one giant
    // response. One request's template/validation failure doesn't block the rest of
    // the batch; each item reports its own outcome.
    [HttpPost("batch")]
    public async Task<IActionResult> CreateBatch([FromBody] List<GeneratePdfRequest> requests)
    {
        const int maxBatchSize = 50;

        var client = HttpContext.Items["Client"] as Tenant;
        var apiKey = HttpContext.Items["ApiKey"] as ApiKey;
        if (client == null)
        {
            return Unauthorized("Tenant not found in context.");
        }

        if (requests == null || requests.Count == 0)
        {
            return BadRequest(new { Error = "Provide at least one document request." });
        }
        if (requests.Count > maxBatchSize)
        {
            return BadRequest(new { Error = $"Batch size ({requests.Count}) exceeds the maximum of {maxBatchSize} documents per call." });
        }

        var results = new List<object>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
        {
            var command = new SubmitPdfJobCommand
            {
                Client = client,
                ApiKey = apiKey ?? new ApiKey { KeyPrefix = "jwt_auth", Environment = "Production" },
                Request = requests[i]
            };

            var result = await _mediator.Send(command);

            results.Add(result.IsSuccess
                ? new { Index = i, Success = true, JobId = result.Value, Error = (string?)null }
                : new { Index = i, Success = false, JobId = (string?)null, Error = result.Error.Message });
        }

        return Accepted(new { Results = results });
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> GetJobStatus(string jobId)
    {
        var callerTenant = HttpContext.Items["Client"] as Tenant;
        var query = new GetPdfJobQuery { JobId = jobId, TenantId = callerTenant?.Id };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess)
        {
            return NotFound(new { Error = result.Error.Message });
        }

        var job = result.Value!;
        PdfEngine.Application.Features.Pdf.Commands.GeneratePdfDiagnostics? diagnostics = null;
        if (!string.IsNullOrEmpty(job.DiagnosticsJson))
        {
            try
            {
                diagnostics = System.Text.Json.JsonSerializer.Deserialize<PdfEngine.Application.Features.Pdf.Commands.GeneratePdfDiagnostics>(job.DiagnosticsJson);
            }
            catch
            {
                // Fallback on deserialization failure
            }
        }

        return Ok(new
        {
            jobId = job.JobId,
            status = job.Status.ToString(),
            createdAt = job.CreatedAt,
            completedAt = job.CompletedAt,
            queueWaitDurationMs = job.QueueWaitDurationMs,
            fileUrl = job.Status == Domain.Enums.PdfJobStatus.Completed ? $"/api/v1/pdf/jobs/{job.JobId}/download" : null,
            error = job.ErrorMessage,
            diagnostics = diagnostics
        });
    }

    [HttpGet("{jobId}/download")]
    public async Task<IActionResult> DownloadJob(string jobId)
    {
        var user = HttpContext.Items["User"] as User;
        var callerTenant = HttpContext.Items["Client"] as Tenant;
        var query = new GetPdfJobQuery { JobId = jobId, TenantId = callerTenant?.Id };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess || result.Value!.Status != Domain.Enums.PdfJobStatus.Completed || string.IsNullOrEmpty(result.Value.FileUrl))
        {
            return NotFound(new { Error = "PDF is not ready or does not exist." });
        }

        var job = result.Value;
        if (user != null && user.Role == "SuperAdmin" && job.TenantId != user.TenantId)
        {
            return StatusCode(403, new { error = "SuperAdmins are restricted from viewing or downloading client-generated PDFs." });
        }

        // Save access audit log
        try
        {
            var dbContext = HttpContext.RequestServices.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
            var accessLog = new DownloadAccessLog
            {
                JobId = jobId,
                UserId = user?.Id,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown",
                UserAgent = HttpContext.Request.Headers["User-Agent"].ToString() ?? "Unknown",
                Timestamp = DateTime.UtcNow
            };
            dbContext.DownloadAccessLogs.Add(accessLog);
            await dbContext.SaveChangesAsync();
        }
        catch
        {
            // Fail silent on logging issue to preserve download behavior
        }

        if (result.Value.FileUrl.StartsWith("http"))
        {
            try
            {
                var stream = await _pdfStorage.GetStreamAsync(result.Value.FileUrl);
                if (stream != null)
                {
                    return File(stream, "application/pdf", $"{result.Value.DocumentName}.pdf");
                }
            }
            catch
            {
                return Redirect(result.Value.FileUrl);
            }
        }

        var streamOnDisk = await _pdfStorage.GetStreamAsync(result.Value.FileUrl);
        if (streamOnDisk == null)
        {
            return NotFound(new { Error = "Physical file not found on disk." });
        }

        return File(streamOnDisk, "application/pdf", $"{result.Value.DocumentName}.pdf");
    }

    [HttpGet("{jobId}/replay")]
    public async Task<IActionResult> GetJobReplayData(string jobId)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var query = new GetPdfJobQuery { JobId = jobId, TenantId = client.Id };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess)
        {
            return NotFound(new { Error = "Job not found." });
        }

        var job = result.Value!;
        if (job.TenantId != client.Id)
        {
            return Forbid("Access to job belongs to a different workspace.");
        }

        var dbContext = HttpContext.RequestServices.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        var snapshot = await dbContext.PdfJobSnapshots.FindAsync(jobId);

        if (snapshot == null)
        {
            return NotFound(new { Error = "Render snapshot not found for this job." });
        }

        return Ok(new
        {
            jobId = snapshot.JobId,
            html = snapshot.Html,
            payloadJson = snapshot.PayloadJson,
            options = snapshot.OptionsJson != null ? System.Text.Json.JsonSerializer.Deserialize<PdfEngine.Application.DTOs.RenderingOptions>(snapshot.OptionsJson) : null,
            environment = snapshot.Environment,
            templateVersion = snapshot.TemplateVersion,
            browserVersion = snapshot.BrowserVersion,
            harJson = snapshot.HarJson
        });
    }

    [HttpPost("{jobId}/replay")]
    public async Task<IActionResult> ReplayJob(string jobId)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var query = new GetPdfJobQuery { JobId = jobId, TenantId = client.Id };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess)
        {
            return NotFound(new { Error = "Job not found." });
        }

        var oldJob = result.Value!;
        if (oldJob.TenantId != client.Id)
        {
            return Forbid("Access to job belongs to a different workspace.");
        }

        var htmlContent = _encryptionService.Decrypt(oldJob.EncryptedHtmlContent);

        // Retrieve original options from snapshot if available
        var options = new PdfEngine.Application.DTOs.RenderingOptions();
        try
        {
            var dbContext = HttpContext.RequestServices.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
            var snapshot = await dbContext.PdfJobSnapshots.FindAsync(jobId);
            if (snapshot != null && !string.IsNullOrEmpty(snapshot.OptionsJson))
            {
                options = System.Text.Json.JsonSerializer.Deserialize<PdfEngine.Application.DTOs.RenderingOptions>(snapshot.OptionsJson) ?? options;
            }
        }
        catch
        {
            // Fallback to default options if snapshot retrieval fails
        }

        var request = new GeneratePdfRequest
        {
            DocumentName = oldJob.DocumentName + "_replay",
            HtmlContent = htmlContent,
            Options = options,
            SourceType = "Replay",
            CorrelationId = oldJob.CorrelationId
        };

        var command = new SubmitPdfJobCommand
        {
            Client = client,
            ApiKey = new ApiKey { KeyPrefix = oldJob.ApiKey, Environment = oldJob.Environment },
            Request = request
        };

        var replayedResult = await _mediator.Send(command);

        if (!replayedResult.IsSuccess)
        {
            return BadRequest(new { Error = replayedResult.Error.Message });
        }

        return Accepted(new { JobId = replayedResult.Value });
    }

    [HttpGet("{jobId}/bundle")]
    public async Task<IActionResult> DownloadJobBundle(string jobId)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var query = new GetPdfJobQuery { JobId = jobId, TenantId = client.Id };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess)
        {
            return NotFound(new { Error = "Job not found." });
        }

        var job = result.Value!;
        if (job.TenantId != client.Id)
        {
            return Forbid("Access to job belongs to a different workspace.");
        }

        var dbContext = HttpContext.RequestServices.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        var snapshot = await dbContext.PdfJobSnapshots.FindAsync(jobId);

        var htmlContent = _encryptionService.Decrypt(job.EncryptedHtmlContent);

        using var memoryStream = new System.IO.MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var htmlEntry = archive.CreateEntry("html.html");
            using (var writer = new System.IO.StreamWriter(htmlEntry.Open()))
            {
                await writer.WriteAsync(htmlContent);
            }

            var diagEntry = archive.CreateEntry("diagnostics.json");
            using (var writer = new System.IO.StreamWriter(diagEntry.Open()))
            {
                await writer.WriteAsync(job.DiagnosticsJson ?? "{}");
            }

            PdfEngine.Application.Features.Pdf.Commands.GeneratePdfDiagnostics? diagnostics = null;
            if (!string.IsNullOrEmpty(job.DiagnosticsJson))
            {
                try
                {
                    diagnostics = System.Text.Json.JsonSerializer.Deserialize<PdfEngine.Application.Features.Pdf.Commands.GeneratePdfDiagnostics>(job.DiagnosticsJson);
                }
                catch {}
            }

            var assetsEntry = archive.CreateEntry("assets.json");
            using (var writer = new System.IO.StreamWriter(assetsEntry.Open()))
            {
                var assetsJson = diagnostics != null 
                    ? System.Text.Json.JsonSerializer.Serialize(diagnostics.Assets) 
                    : "[]";
                await writer.WriteAsync(assetsJson);
            }

            var harJson = snapshot?.HarJson ?? diagnostics?.HarJson;
            if (!string.IsNullOrEmpty(harJson))
            {
                var harEntry = archive.CreateEntry("render.har");
                using (var writer = new System.IO.StreamWriter(harEntry.Open()))
                {
                    await writer.WriteAsync(harJson);
                }
            }

            var screenshotBase64 = diagnostics?.ScreenshotBase64;
            if (!string.IsNullOrEmpty(screenshotBase64))
            {
                try
                {
                    var screenshotBytes = Convert.FromBase64String(screenshotBase64);
                    var screenshotEntry = archive.CreateEntry("screenshot.png");
                    using (var stream = screenshotEntry.Open())
                    {
                        await stream.WriteAsync(screenshotBytes, 0, screenshotBytes.Length);
                    }
                }
                catch {}
            }
        }

        memoryStream.Position = 0;
        return File(memoryStream.ToArray(), "application/zip", $"job-{jobId}.zip");
    }
}

