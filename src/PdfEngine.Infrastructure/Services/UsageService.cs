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

    public async Task TrackUsageAsync(Guid tenantId, Guid? apiKeyId, string requestId, int pdfSize, int durationMs, int statusCode, bool success, string? errorMessage = null)
    {
        // Simple internal cost calculation: $0.0001 per 100ms + $0.0001 per MB
        decimal cost = (durationMs / 1000m * 0.001m) + (pdfSize / 1024m / 1024m * 0.001m);

        var record = new UsageRecord
        {
            TenantId = tenantId,
            ApiKeyId = apiKeyId,
            RequestId = requestId,
            Timestamp = DateTime.UtcNow,
            PdfSizeBytes = pdfSize,
            DurationMs = durationMs,
            StatusCode = statusCode,
            Success = success,
            ErrorMessage = errorMessage,
            Cost = cost
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
