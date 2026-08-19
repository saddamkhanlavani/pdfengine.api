using System;
using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IUsageService
{
    Task TrackUsageAsync(
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
        string? fileUrl = null);
    Task<int> GetUsageThisMonthAsync(Guid tenantId, DateTime billingCycleStart);
}
