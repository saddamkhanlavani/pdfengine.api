using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Storage;

public class LocalDiskPdfStorage : IPdfStorage
{
    private readonly string _storageDirectory;
    private readonly ILogger<LocalDiskPdfStorage> _logger;

    public LocalDiskPdfStorage(ILogger<LocalDiskPdfStorage> logger)
    {
        _logger = logger;
        _storageDirectory = Path.Combine(Path.GetTempPath(), "pdfengine_storage");
        
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
        }
    }

    public async Task<string> SaveAsync(byte[] pdfBytes, string jobId, string documentName)
    {
        var fileName = $"{jobId}.pdf";
        var filePath = Path.Combine(_storageDirectory, fileName);

        await File.WriteAllBytesAsync(filePath, pdfBytes);
        
        _logger.LogInformation("Saved PDF for Job {JobId} to {FilePath}", jobId, filePath);

        // In a real S3 implementation, this would return an https:// URL.
        // Here, we just return the local file path as the "Url"
        return filePath;
    }

    public Task<Stream?> GetStreamAsync(string fileUrl)
    {
        if (!File.Exists(fileUrl))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(new FileStream(fileUrl, FileMode.Open, FileAccess.Read, FileShare.Read));
    }
}
