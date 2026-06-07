using System;
using Microsoft.AspNetCore.Http;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Security;

public class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null) return null;

            // 1. Try context items (set by middleware)
            if (httpContext.Items.TryGetValue("TenantId", out var itemVal) && itemVal is Guid tenantIdVal)
            {
                return tenantIdVal;
            }

            // 2. Try claims
            var tenantClaim = httpContext.User.FindFirst("tenantId")?.Value;
            if (!string.IsNullOrEmpty(tenantClaim) && Guid.TryParse(tenantClaim, out var claimGuid))
            {
                return claimGuid;
            }

            // 3. Try ApiKey tenant if injected
            if (httpContext.Items.TryGetValue("Client", out var clientObj) && clientObj is PdfEngine.Domain.Entities.Tenant t)
            {
                return t.Id;
            }

            return null;
        }
    }
}
