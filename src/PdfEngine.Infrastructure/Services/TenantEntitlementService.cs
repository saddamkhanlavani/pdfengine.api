using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Services;

public class TenantEntitlementService : ITenantEntitlementService
{
    private readonly PdfEngineDbContext _dbContext;

    public TenantEntitlementService(PdfEngineDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId)
    {
        // Use IgnoreQueryFilters so we can fetch this regardless of the current tenant context scope if needed (e.g. queue worker or admin check)
        return await _dbContext.TenantEntitlements
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId);
    }

    public async Task<string?> GetTenantAdminEmailAsync(Guid tenantId)
    {
        return await _dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && (u.Role == "Admin" || u.Role == "SuperAdmin"))
            .Select(u => u.Email)
            .FirstOrDefaultAsync();
    }
}
