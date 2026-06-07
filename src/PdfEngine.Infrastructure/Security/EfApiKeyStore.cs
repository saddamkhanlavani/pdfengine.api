using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Security;

public class EfApiKeyStore : IApiKeyStore
{
    private readonly PdfEngineDbContext _context;

    public EfApiKeyStore(PdfEngineDbContext context)
    {
        _context = context;
    }

    public async Task<ApiKey?> GetApiKeyAsync(string apiKey)
    {
        var hash = HashHelper.ComputeSha256Hash(apiKey);
        return await _context.ApiKeys
            .Include(a => a.Tenant)
            .FirstOrDefaultAsync(a => a.KeyHash == hash && !a.IsRevoked);
    }
}
