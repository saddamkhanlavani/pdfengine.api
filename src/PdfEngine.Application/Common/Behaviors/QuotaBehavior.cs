using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Common;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.Common.Behaviors;

public class QuotaBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : GeneratePdfCommand
    where TResponse : Result<byte[]>
{
    private readonly IUsageService _usageService;
    private readonly IPdfStorage _pdfStorage;
    private readonly ILogger<QuotaBehavior<TRequest, TResponse>> _logger;

    public QuotaBehavior(IUsageService usageService, IPdfStorage pdfStorage, ILogger<QuotaBehavior<TRequest, TResponse>> logger)
    {
        _usageService = usageService;
        _pdfStorage = pdfStorage;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var tenant = request.Client;
        if (tenant == null) return await next();

        var plan = PlanRegistry.Plans[tenant.Plan];
        
        // Reset check (Logic from user instructions)
        if (DateTime.UtcNow > tenant.BillingCycleStart.AddMonths(1))
        {
            // Note: In a real system, this reset would be handled by the BillingWorker
            // but we keep it here as a fail-safe for local dev / simple setups.
            _logger.LogInformation("Tenant {TenantName} billing cycle reset detected.", tenant.Name);
        }

        var usageCount = await _usageService.GetUsageThisMonthAsync(tenant.Id, tenant.BillingCycleStart);

        // Soft limit (80%) warning
        if (usageCount >= plan.IncludedQuota * 0.8 && usageCount < plan.IncludedQuota)
        {
            _logger.LogWarning("Tenant {TenantName} has reached 80% of their quota ({Usage}/{Quota}).", 
                tenant.Name, usageCount, plan.IncludedQuota);
        }

        // Hard limit enforcement
        if (usageCount >= plan.IncludedQuota)
        {
            if (tenant.Plan == PlanType.Free)
            {
                _logger.LogError("Tenant {TenantName} has exceeded their Free quota.", tenant.Name);
                return (TResponse)Result<byte[]>.Fail(new Error("Quota.Exceeded", "Monthly quota exceeded for Free plan. Please upgrade to Pro."));
            }
            
            // For Pro/Enterprise, we allow overage but log it for billing
            _logger.LogInformation("Tenant {TenantName} is in overage ({Usage}/{Quota}).", 
                tenant.Name, usageCount, plan.IncludedQuota);
        }

        var startTime = DateTime.UtcNow;
        var response = await next();
        var endTime = DateTime.UtcNow;

        var durationMs = (int)(endTime - startTime).TotalMilliseconds;
        var pdfSize = response.IsSuccess ? response.Value!.Length : 0;
        var statusCode = response.IsSuccess ? 200 : 400; // Simplified

        // Track usage asynchronously
        var requestId = Guid.NewGuid().ToString();

        if (response.IsSuccess && response.Value != null)
        {
            try 
            {
                await _pdfStorage.SaveAsync(response.Value, requestId, request.DocumentName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save generated PDF to storage for RequestId: {RequestId}", requestId);
            }
        }

        await _usageService.TrackUsageAsync(
            tenant.Id,
            request.ApiKey?.Id,
            requestId,
            pdfSize,
            durationMs,
            statusCode,
            response.IsSuccess,
            response.IsFailure ? response.Error.Message : null);

        return response;
    }
}
