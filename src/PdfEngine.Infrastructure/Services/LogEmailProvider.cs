using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class LogEmailProvider : IEmailProvider
{
    private readonly ILogger<LogEmailProvider> _logger;

    public LogEmailProvider(ILogger<LogEmailProvider> logger)
    {
        _logger = logger;
    }

    public Task SendEmailAsync(string to, string subject, string body)
    {
        _logger.LogInformation(
            "*** EMAIL SENT (LOG PROVIDER) ***\nTo: {To}\nSubject: {Subject}\nBody:\n{Body}\n*********************************",
            to,
            subject,
            body);

        return Task.CompletedTask;
    }
}
