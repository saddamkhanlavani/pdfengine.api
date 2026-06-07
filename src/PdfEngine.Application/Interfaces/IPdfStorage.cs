using System.IO;
using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IPdfStorage
{
    Task<string> SaveAsync(byte[] pdfBytes, string jobId, string documentName);
    Task<Stream?> GetStreamAsync(string fileUrl);
}
