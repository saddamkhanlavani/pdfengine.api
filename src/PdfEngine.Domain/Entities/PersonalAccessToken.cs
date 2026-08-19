using System;

namespace PdfEngine.Domain.Entities;

public class PersonalAccessToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty; // e.g. "CLI Token"
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    // Navigation
    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
}
