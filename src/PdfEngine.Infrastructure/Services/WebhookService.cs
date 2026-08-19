using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Services;

public class WebhookService : IWebhookService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebhookService> _logger;
    private static readonly HttpClient HttpClientInstance = new() { Timeout = TimeSpan.FromSeconds(15) };

    public WebhookService(IServiceProvider serviceProvider, ILogger<WebhookService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task DispatchAsync(Guid tenantId, string eventType, object payload)
    {
        // Fire-and-forget in the background to avoid blocking API thread
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngineDbContext>();

                // Get active endpoints
                var endpoints = await dbContext.WebhookEndpoints
                    .Where(e => e.TenantId == tenantId && e.DeletedAt == null)
                    .ToListAsync();

                var matchingEndpoints = endpoints
                    .Where(e => string.IsNullOrEmpty(e.Events) || 
                                e.Events.Split(',').Contains(eventType, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (!matchingEndpoints.Any()) return;

                var payloadJson = JsonSerializer.Serialize(new
                {
                    @event = eventType,
                    data = payload,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });

                foreach (var endpoint in matchingEndpoints)
                {
                    await SendWebhookWithRetryAsync(dbContext, endpoint, eventType, payloadJson);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dispatch webhook event {Event} for tenant {TenantId}", eventType, tenantId);
            }
        });

        return Task.CompletedTask;
    }

    private async Task SendWebhookWithRetryAsync(PdfEngineDbContext dbContext, WebhookEndpoint endpoint, string eventType, string payloadJson)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signatureInput = $"{timestamp}.{payloadJson}";
        var signature = ComputeHmacSha256(signatureInput, endpoint.Secret);

        int maxRetries = 3;
        int delayMs = 1000;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var delivery = new WebhookDelivery
            {
                EndpointId = endpoint.Id,
                Event = eventType,
                Payload = payloadJson,
                Timestamp = DateTime.UtcNow,
                AttemptCount = attempt
            };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                
                // Set anti-replay signature headers
                request.Headers.Add("X-PdfEngine-Timestamp", timestamp);
                request.Headers.Add("X-PdfEngine-Signature", signature);
                request.Headers.Add("User-Agent", "PdfEngine-Webhook-Dispatcher/1.0");

                var response = await HttpClientInstance.SendAsync(request);
                stopwatch.Stop();

                delivery.LatencyMs = stopwatch.ElapsedMilliseconds;
                delivery.ResponseStatusCode = (int)response.StatusCode;
                delivery.ResponsePayload = await response.Content.ReadAsStringAsync();
                delivery.IsSuccess = response.IsSuccessStatusCode;

                // Log details
                _logger.LogInformation("Webhook to {Url} status: {Status} (attempt {Attempt}) in {Latency}ms", endpoint.Url, response.StatusCode, attempt, delivery.LatencyMs);

                dbContext.WebhookDeliveries.Add(delivery);
                await dbContext.SaveChangesAsync();

                if (response.IsSuccessStatusCode)
                {
                    break; // break retry loop
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                delivery.LatencyMs = stopwatch.ElapsedMilliseconds;
                delivery.IsSuccess = false;
                delivery.ResponsePayload = ex.Message;
                delivery.ResponseStatusCode = 500;

                _logger.LogWarning(ex, "Webhook dispatch failed to {Url} on attempt {Attempt} in {Latency}ms", endpoint.Url, attempt, delivery.LatencyMs);

                dbContext.WebhookDeliveries.Add(delivery);
                await dbContext.SaveChangesAsync();
            }

            if (attempt < maxRetries)
            {
                await Task.Delay(delayMs * attempt); // Linear/Exponential backoff
            }
        }
    }

    private string ComputeHmacSha256(string message, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        using var hmac = new HMACSHA256(keyBytes);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hashBytes = hmac.ComputeHash(messageBytes);
        return Convert.ToHexString(hashBytes).ToLower();
    }
}
