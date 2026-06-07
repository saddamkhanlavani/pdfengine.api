using System.Threading.Tasks;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Interfaces;

public interface IPdfJobStorage
{
    Task SaveJobAsync(PdfJob job);
    Task<PdfJob?> GetJobAsync(string jobId);
    Task UpdateJobAsync(PdfJob job);
}
