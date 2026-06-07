using System;
using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IApiKeyService
{
    Task<string> RotateApiKeyAsync(Guid tenantId, string environment = "Production", string scopes = "render:pdf,logs:read", string? ipWhitelist = null);
    Task RevokeApiKeyAsync(Guid tenantId, Guid keyId);
}
