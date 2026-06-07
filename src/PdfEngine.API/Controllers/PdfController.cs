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
            Options = request.Options
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
            ErrorCodes.HtmlTooLarge => BadRequest(errorResponse),
            ErrorCodes.RequestAborted => BadRequest(errorResponse),
            ErrorCodes.RenderTimeout => StatusCode(408, errorResponse),
            ErrorCodes.BrowserUnavailable => StatusCode(503, errorResponse),
            _ => StatusCode(500, errorResponse)
        };
    }
}
