using System;
using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IUsageService
{
    Task TrackUsageAsync(Guid tenantId, Guid? apiKeyId, string requestId, int pdfSize, int durationMs, int statusCode, bool success, string? errorMessage = null);
    Task<int> GetUsageThisMonthAsync(Guid tenantId, DateTime billingCycleStart);
}
