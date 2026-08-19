using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.Infrastructure.Services;

public class UsageService : IUsageService
{
    private readonly PdfEngineDbContext _context;

    public UsageService(PdfEngineDbContext context)
    {
        _context = context;
    }

    public async Task TrackUsageAsync(
        Guid tenantId, 
        Guid? apiKeyId, 
        string requestId, 
        string documentName,
        int pdfSize, 
        int durationMs, 
        int statusCode, 
        bool success, 
        string? errorMessage = null,
        string? clientIp = null,
        string? userAgent = null,
        string? authMechanism = null,
        bool isWatermarked = false,
        string? sandboxEnvironment = null,
        string? fileUrl = null)
    {
        // Cost model: base $0.00010 + CPU time ($0.00005 / 100ms) + storage transfer ($0.000008 / KB)
        decimal cost = 0.00010m + (durationMs * 0.0000005m) + ((pdfSize / 1024m) * 0.000008m);
        cost = Math.Round(cost, 6);

        var record = new UsageRecord
        {
            TenantId = tenantId,
            ApiKeyId = apiKeyId,
            RequestId = requestId,
            DocumentName = documentName,
            Timestamp = DateTime.UtcNow,
            PdfSizeBytes = pdfSize,
            DurationMs = durationMs,
            StatusCode = statusCode,
            Success = success,
            ErrorMessage = errorMessage,
            Cost = cost,
            ClientIp = clientIp,
            UserAgent = userAgent,
            AuthMechanism = authMechanism,
            IsWatermarked = isWatermarked,
            SandboxEnvironment = sandboxEnvironment,
            FileUrl = fileUrl,
            AssetsWaterfall = "[]",
            EncryptedHtmlSnapshot = "",
            Environment = apiKeyId.HasValue ? (_context.ApiKeys.Find(apiKeyId.Value)?.Environment ?? "Production") : "Production"
        };

        _context.UsageRecords.Add(record);

        // Update API Key last used timestamp
        if (apiKeyId.HasValue)
        {
            var key = await _context.ApiKeys.FindAsync(apiKeyId.Value);
            if (key != null)
            {
                key.LastUsedAt = DateTime.UtcNow;
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task<int> GetUsageThisMonthAsync(Guid tenantId, DateTime billingCycleStart)
    {
        return await _context.UsageRecords
            .Where(x => x.TenantId == tenantId && x.Timestamp >= billingCycleStart && x.Success)
            .CountAsync();
    }
}
