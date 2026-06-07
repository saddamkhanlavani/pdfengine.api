namespace PdfEngine.Application.DTOs;

public class GeneratePdfResponse
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? PdfBytes { get; set; }
    
    public static GeneratePdfResponse Success(byte[] bytes) => new() { IsSuccess = true, PdfBytes = bytes };
    public static GeneratePdfResponse Failure(string error) => new() { IsSuccess = false, ErrorMessage = error };
}
