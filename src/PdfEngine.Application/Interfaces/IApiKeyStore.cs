using System.Threading.Tasks;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Interfaces;

public interface IApiKeyStore
{
    Task<ApiKey?> GetApiKeyAsync(string apiKey);
}
