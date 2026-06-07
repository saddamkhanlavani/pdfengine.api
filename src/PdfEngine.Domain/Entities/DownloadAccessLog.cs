using System;

namespace PdfEngine.Domain.Entities;

public class DownloadAccessLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string JobId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
