using System;

namespace PdfEngine.Domain.Entities;

public class ProcessedWebhookEvent
{
    public string Id { get; set; } = string.Empty; // Stripe Event ID
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;
}
