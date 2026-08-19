using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PdfEngine.Application.Configurations;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class SendGridEmailProvider : IEmailProvider
{
    private static readonly HttpClient HttpClient = new();
    private readonly EmailOptions _options;
    private readonly ILogger<SendGridEmailProvider> _logger;

    public SendGridEmailProvider(IOptions<EmailOptions> options, ILogger<SendGridEmailProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendEmailAsync(string to, string subject, string body)
    {
        var apiKey = _options.SendGrid.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("SendGrid API Key is not configured. Falling back to log print.");
            _logger.LogInformation("SendGrid Fallback email to: {To}, Subject: {Subject}", to, subject);
            return;
        }

        var payload = new
        {
            personalizations = new[]
            {
                new { to = new[] { new { email = to } } }
            },
            from = new { email = _options.SenderEmail, name = _options.SenderName },
            subject = subject,
            content = new[]
            {
                new { type = "text/html", value = body }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to send email via SendGrid: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"SendGrid API error: {response.StatusCode} - {errorContent}");
        }
    }
}
