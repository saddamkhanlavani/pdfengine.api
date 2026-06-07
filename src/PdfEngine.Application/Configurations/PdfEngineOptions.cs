namespace PdfEngine.Application.Configurations;

public class PdfEngineOptions
{
    public const string SectionName = "PdfEngine";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxHtmlLength { get; set; } = 2000000;
    public bool EnableDetailedErrors { get; set; } = false;
    public int MaxConcurrentRenders { get; set; } = 4;
}
