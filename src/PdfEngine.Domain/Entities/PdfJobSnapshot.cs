using System;

namespace PdfEngine.Domain.Entities;

public class PdfJobSnapshot
{
    public string JobId { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public string? OptionsJson { get; set; }
    public string Environment { get; set; } = "Production";
    public string? TemplateVersion { get; set; }
    public string? BrowserVersion { get; set; }
    public string? HarJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
