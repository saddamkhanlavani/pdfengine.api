using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Security;

public class ApiKeyService : IApiKeyService
{
    private readonly PdfEngineDbContext _context;

    public ApiKeyService(PdfEngineDbContext context)
    {
        _context = context;
    }

    public async Task<string> RotateApiKeyAsync(Guid tenantId, string environment = "Production", string scopes = "render:pdf,logs:read", string? ipWhitelist = null)
    {
        var tenant = await _context.Tenants.FindAsync(tenantId);
        if (tenant == null) throw new Exception("Tenant not found");

        // Revoke old keys in this environment
        var oldKeys = await _context.ApiKeys
            .Where(k => k.TenantId == tenantId && k.Environment == environment && !k.IsRevoked)
            .ToListAsync();
        
        foreach (var k in oldKeys) k.IsRevoked = true;

        // Generate new key
        var isProd = environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
        var envPrefix = isProd ? "live" : "test";
        var prefixSegment = GenerateRandomString(6);
        var secretSegment = GenerateRandomString(24);
        var rawKey = $"pk_{envPrefix}_{prefixSegment}_{secretSegment}";
        
        var keyPrefix = $"pk_{envPrefix}_{prefixSegment}";
        var keyHash = HashHelper.ComputeSha256Hash(rawKey);
        var maskedKey = $"{keyPrefix}_{new string('*', 12)}";

        var apiKeyEntity = new ApiKey
        {
            TenantId = tenantId,
            Key = maskedKey,
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            IsRevoked = false,
            Environment = environment,
            Scopes = scopes,
            IpWhitelist = ipWhitelist
        };

        _context.ApiKeys.Add(apiKeyEntity);
        await _context.SaveChangesAsync();

        return rawKey;
    }

    public async Task RevokeApiKeyAsync(Guid tenantId, Guid keyId)
    {
        var keyRecord = await _context.ApiKeys
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Id == keyId);

        if (keyRecord != null)
        {
            keyRecord.IsRevoked = true;
            await _context.SaveChangesAsync();
        }
    }

    private string GenerateRandomString(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "")
            .Substring(0, length)
            .ToLower();
    }
}
