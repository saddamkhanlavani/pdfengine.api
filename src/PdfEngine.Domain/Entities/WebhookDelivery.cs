using System;

namespace PdfEngine.Domain.Entities;

public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EndpointId { get; set; }
    public WebhookEndpoint Endpoint { get; set; } = null!;
    
    public string Event { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty; // JSON payload
    
    public int ResponseStatusCode { get; set; }
    public bool IsSuccess { get; set; }
    public int AttemptCount { get; set; }
    
    public string? ResponsePayload { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
