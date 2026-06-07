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

    public JobsController(IMediator mediator, IPdfStorage pdfStorage)
    {
        _mediator = mediator;
        _pdfStorage = pdfStorage;
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
            return BadRequest(new { Error = result.Error.Message });
        }

        return Accepted(new { JobId = result.Value });
    }

    [HttpGet("{jobId}")]
    public async Task<IActionResult> GetJobStatus(string jobId)
    {
        var query = new GetPdfJobQuery { JobId = jobId };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess)
        {
            return NotFound(new { Error = result.Error.Message });
        }

        var job = result.Value!;
        return Ok(new
        {
            jobId = job.JobId,
            status = job.Status.ToString(),
            createdAt = job.CreatedAt,
            completedAt = job.CompletedAt,
            fileUrl = job.Status == Domain.Enums.PdfJobStatus.Completed ? $"/api/pdf/jobs/{job.JobId}/download" : null,
            error = job.ErrorMessage
        });
    }

    [HttpGet("{jobId}/download")]
    public async Task<IActionResult> DownloadJob(string jobId)
    {
        var query = new GetPdfJobQuery { JobId = jobId };
        var result = await _mediator.Send(query);

        if (!result.IsSuccess || result.Value!.Status != Domain.Enums.PdfJobStatus.Completed || string.IsNullOrEmpty(result.Value.FileUrl))
        {
            return NotFound(new { Error = "PDF is not ready or does not exist." });
        }

        // Save access audit log
        try
        {
            var dbContext = HttpContext.RequestServices.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
            var user = HttpContext.Items["User"] as User;
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
            return Redirect(result.Value.FileUrl);
        }

        var stream = await _pdfStorage.GetStreamAsync(result.Value.FileUrl);
        if (stream == null)
        {
            return NotFound(new { Error = "Physical file not found on disk." });
        }

        return File(stream, "application/pdf", $"{result.Value.DocumentName}.pdf");
    }
}
