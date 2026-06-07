using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace PdfEngine.Infrastructure.Interfaces;

public interface IBrowserManager
{
    Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken = default);
    bool IsBrowserAlive();
}
