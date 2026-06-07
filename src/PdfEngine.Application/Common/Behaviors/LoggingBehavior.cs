using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Common;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.Common.Behaviors;

public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        
        // Logging properties conditionally to avoid reflection over huge strings
        if (request is GeneratePdfCommand pdfCommand)
        {
            _logger.LogInformation("Handling {RequestName} - DocumentName: {DocumentName}, HtmlLength: {HtmlLength}",
                requestName, pdfCommand.DocumentName, pdfCommand.HtmlContent.Length);
        }
        else
        {
            _logger.LogInformation("Handling {RequestName}", requestName);
        }

        var response = await next();

        if (response is Result<byte[]> result)
        {
            if (result.IsSuccess)
            {
                _logger.LogInformation("Handled {RequestName} successfully.", requestName);
            }
            else
            {
                _logger.LogWarning("Handled {RequestName} completed with failure: {ErrorCode} - {ErrorMessage}", 
                    requestName, result.Error.Code, result.Error.Message);
            }
        }
        else
        {
            _logger.LogInformation("Handled {RequestName} successfully.", requestName);
        }

        return response;
    }
}
