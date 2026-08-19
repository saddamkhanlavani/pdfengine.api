using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PdfEngine.Application.Configurations;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Services;

public class PostmarkEmailProvider : IEmailProvider
{
    private static readonly HttpClient HttpClient = new();
    private readonly EmailOptions _options;
    private readonly ILogger<PostmarkEmailProvider> _logger;

    public PostmarkEmailProvider(IOptions<EmailOptions> options, ILogger<PostmarkEmailProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendEmailAsync(string to, string subject, string body)
    {
        var token = _options.Postmark.ServerToken;
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("Postmark Server Token is not configured. Falling back to log print.");
            _logger.LogInformation("Postmark Fallback email to: {To}, Subject: {Subject}", to, subject);
            return;
        }

        var payload = new
        {
            From = $"{_options.SenderName} <{_options.SenderEmail}>",
            To = to,
            Subject = subject,
            HtmlBody = body,
            MessageStream = "outbound"
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.postmarkapp.com/email")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Postmark-Server-Token", token);
        request.Headers.Add("Accept", "application/json");

        var response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("Failed to send email via Postmark: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Postmark API error: {response.StatusCode} - {errorContent}");
        }
    }
}
