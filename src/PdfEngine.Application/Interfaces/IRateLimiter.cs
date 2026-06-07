using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IRateLimiter
{
    Task<bool> IsAllowedAsync(string apiKey, int limitPerMinute);
}
