using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediatR;
using PdfEngine.API.Contracts;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Domain.Entities;

namespace PdfEngine.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PdfController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ILogger<PdfController> _logger;

    public PdfController(ISender sender, ILogger<PdfController> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    [HttpPost("generate")]
    public async Task<IActionResult> GeneratePdf([FromBody] GeneratePdfRequest request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope("TraceId: {TraceId}", HttpContext.TraceIdentifier);

        var client = HttpContext.Items["Client"] as Tenant;
        var apiKey = HttpContext.Items["ApiKey"] as ApiKey;

        var command = new GeneratePdfCommand
        {
            Client = client!,
            ApiKey = apiKey,
            DocumentName = request.DocumentName,
            DocumentType = request.DocumentType,
            HtmlContent = request.HtmlContent,
            Url = request.Url,
            TemplateData = request.TemplateData,
            Options = request.Options
        };

        var result = await _sender.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            Response.Headers.Append("X-Render-Job-Id", command.JobId);
            Response.Headers.Append("X-Render-Score", command.Diagnostics.RenderScore.ToString());
            Response.Headers.Append("X-Render-Pages", command.Diagnostics.Pages.ToString());
            Response.Headers.Append("X-Render-Duration", command.Diagnostics.DurationMs.ToString());
            Func<System.Collections.Generic.List<string>, System.Collections.Generic.List<string>> limitLogs = list =>
            {
                if (list == null) return new System.Collections.Generic.List<string>();
                var result = new System.Collections.Generic.List<string>();
                for (int i = 0; i < Math.Min(list.Count, 10); i++)
                {
                    var val = list[i];
                    if (val.Length > 150) val = val.Substring(0, 147) + "...";
                    result.Add(val);
                }
                return result;
            };

            var headerDiagnostics = new {
                jobId = command.JobId,
                warnings = limitLogs(command.Diagnostics.Warnings),
                jsErrors = limitLogs(command.Diagnostics.JsErrors),
                consoleWarnings = limitLogs(command.Diagnostics.ConsoleWarnings),
                embeddedFonts = limitLogs(command.Diagnostics.EmbeddedFonts),
                renderScore = command.Diagnostics.RenderScore,
                layoutScore = command.Diagnostics.LayoutScore,
                assetScore = command.Diagnostics.AssetScore,
                performanceScore = command.Diagnostics.PerformanceScore,
                consistencyScore = command.Diagnostics.ConsistencyScore,
                pages = command.Diagnostics.Pages,
                fileSize = command.Diagnostics.FileSize,
                durationMs = command.Diagnostics.DurationMs,
                visualDrift = command.Diagnostics.VisualDrift,
                estimatedCost = command.Diagnostics.EstimatedCost
            };
            Response.Headers.Append("X-Render-Diagnostics", System.Text.Json.JsonSerializer.Serialize(headerDiagnostics));

            // Gate J: every PDF states the exact stack that produced it, so a caller can
            // record it alongside the output and later pin it via `pinEngineVersion`.
            // Reproducibility is unverifiable without this being a real, returned value.
            Response.Headers.Append("X-PdfEngine-Engine-Version",
                PdfEngine.Application.Common.EngineVersion.Current);

            return File(result.Value!, "application/pdf", $"{request.DocumentName}.pdf");
        }


        var errorResponse = new ErrorResponse
        {
            Code = result.Error.Code,
            Message = result.Error.Message,
            TraceId = HttpContext.TraceIdentifier
        };

        return result.Error.Code switch
        {
            ErrorCodes.Validation => BadRequest(errorResponse),
            ErrorCodes.HtmlTooLarge => BadRequest(errorResponse),
            ErrorCodes.RequestAborted => BadRequest(errorResponse),
            ErrorCodes.RenderTimeout => StatusCode(408, errorResponse),
            ErrorCodes.BrowserUnavailable => StatusCode(503, errorResponse),
            ErrorCodes.QuotaExceeded => StatusCode(429, errorResponse),
            ErrorCodes.BlockedUrl => BadRequest(errorResponse),
            _ => StatusCode(500, errorResponse)
        };
    }

    /// <summary>
    /// Mechanical page operations on an already-rendered PDF (T2-4). Merge has its own
    /// endpoint because it takes many files; these all take one.
    /// </summary>
    [HttpPost("transform")]
    public async Task<IActionResult> TransformPdf([FromBody] TransformPdfRequest request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope("TraceId: {TraceId}", HttpContext.TraceIdentifier);

        var command = new TransformPdfCommand
        {
            DocumentName = request.DocumentName,
            File = request.File,
            Operation = request.Operation,
            Pages = request.Pages,
            Rotation = request.Rotation,
            PagesPerSheet = request.PagesPerSheet
        };

        var result = await _sender.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            return File(result.Value!, "application/pdf", $"{request.DocumentName}.pdf");
        }

        var errorResponse = new ErrorResponse
        {
            Code = result.Error.Code,
            Message = result.Error.Message,
            TraceId = HttpContext.TraceIdentifier
        };

        return result.Error.Code switch
        {
            ErrorCodes.Validation => BadRequest(errorResponse),
            _ => StatusCode(500, errorResponse)
        };
    }

    [HttpPost("merge")]
    public async Task<IActionResult> MergePdf([FromBody] MergePdfRequest request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope("TraceId: {TraceId}", HttpContext.TraceIdentifier);

        var command = new MergePdfCommand
        {
            DocumentName = request.DocumentName,
            Files = request.Files
        };

        var result = await _sender.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            return File(result.Value!, "application/pdf", $"{request.DocumentName}.pdf");
        }

        var errorResponse = new ErrorResponse
        {
            Code = result.Error.Code,
            Message = result.Error.Message,
            TraceId = HttpContext.TraceIdentifier
        };

        return result.Error.Code switch
        {
            ErrorCodes.Validation => BadRequest(errorResponse),
            _ => StatusCode(500, errorResponse)
        };
    }
}
