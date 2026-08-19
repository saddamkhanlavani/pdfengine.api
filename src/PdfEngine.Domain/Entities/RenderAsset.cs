using System;

namespace PdfEngine.Domain.Entities;

public class RenderAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RequestId { get; set; } = string.Empty; // links to UsageRecord RequestId
    public string AssetUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int DurationMs { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
