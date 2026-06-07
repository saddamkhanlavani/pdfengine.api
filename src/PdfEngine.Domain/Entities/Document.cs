using System;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Domain.Entities;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DocumentType Type { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
