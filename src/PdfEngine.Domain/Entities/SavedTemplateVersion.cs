using System;

namespace PdfEngine.Domain.Entities;

public class SavedTemplateVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SavedTemplateId { get; set; }
    public SavedTemplate SavedTemplate { get; set; } = null!;
    
    public int VersionNumber { get; set; }
    public string HtmlContent { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ReferenceScreenshotBase64 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
