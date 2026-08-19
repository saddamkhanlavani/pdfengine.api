using System;

namespace PdfEngine.Domain.Entities;

public class SavedTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string Name { get; set; } = string.Empty;
    public string HtmlContent { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; } // Soft delete flag

    public string Environment { get; set; } = "Production"; // "Production" or "Development"

    public bool IsArchived { get; set; }
    public Guid? ForkedFromId { get; set; }

    // AI Readiness Metadata Fields
    public string? TemplateType { get; set; }
    public string? Industry { get; set; }
    public int EstimatedPageCount { get; set; }

    public System.Collections.Generic.ICollection<SavedTemplateVersion> Versions { get; set; } = new System.Collections.Generic.List<SavedTemplateVersion>();
}
