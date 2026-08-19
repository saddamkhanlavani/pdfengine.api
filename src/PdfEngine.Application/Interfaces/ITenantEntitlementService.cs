using System;
using System.Threading.Tasks;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Interfaces;

public interface ITenantEntitlementService
{
    Task<TenantEntitlement?> GetEntitlementAsync(Guid tenantId);
    Task<string?> GetTenantAdminEmailAsync(Guid tenantId);
}
