namespace PdfEngine.Application.DTOs;

public class RenderingOptions
{
    public string PageSize { get; set; } = "A4";
    public string MarginTop { get; set; } = "0px";
    public string MarginBottom { get; set; } = "0px";
    public string MarginLeft { get; set; } = "0px";
    public string MarginRight { get; set; } = "0px";
    public bool PrintBackground { get; set; } = true;
}
