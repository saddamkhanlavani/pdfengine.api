using System;

namespace PdfEngine.Domain.Entities;

public class FeatureFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string TargetPlan { get; set; } = "Free"; // Free, Pro, Enterprise
}
