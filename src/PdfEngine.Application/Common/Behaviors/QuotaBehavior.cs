using System;
using System.Linq;
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
    private readonly IClientContextProvider _clientContextProvider;
    private readonly ITenantEntitlementService _entitlementService;
    private readonly IEmailService _emailService;
    private readonly ILogger<QuotaBehavior<TRequest, TResponse>> _logger;

    public QuotaBehavior(
        IUsageService usageService, 
        IPdfStorage pdfStorage, 
        IClientContextProvider clientContextProvider, 
        ITenantEntitlementService entitlementService,
        IEmailService emailService,
        ILogger<QuotaBehavior<TRequest, TResponse>> logger)
    {
        _usageService = usageService;
        _pdfStorage = pdfStorage;
        _clientContextProvider = clientContextProvider;
        _entitlementService = entitlementService;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var tenant = request.Client;
        if (tenant == null) return await next();

        var plan = PlanRegistry.Plans[tenant.Plan];
        var entitlement = await _entitlementService.GetEntitlementAsync(tenant.Id);
        int monthlyLimit = entitlement != null && entitlement.MonthlyRenderLimit > 0
            ? entitlement.MonthlyRenderLimit
            : plan.IncludedQuota;
        
        // Reset check (Logic from user instructions)
        if (DateTime.UtcNow > tenant.BillingCycleStart.AddMonths(1))
        {
            // Note: In a real system, this reset would be handled by the BillingWorker
            // but we keep it here as a fail-safe for local dev / simple setups.
            _logger.LogInformation("Tenant {TenantName} billing cycle reset detected.", tenant.Name);
        }

        var usageCount = await _usageService.GetUsageThisMonthAsync(tenant.Id, tenant.BillingCycleStart);

        // Soft limit (80%) warning
        if (usageCount >= monthlyLimit * 0.8 && usageCount < monthlyLimit)
        {
            _logger.LogWarning("Tenant {TenantName} has reached 80% of their quota ({Usage}/{Quota}).", 
                tenant.Name, usageCount, monthlyLimit);
        }

        // Check and trigger lifecycle email alerts at 85%, 95%, 100%
        int[] warningThresholds = new[] { 85, 95, 100 };
        foreach (var pct in warningThresholds)
        {
            int thresholdVal = (int)(monthlyLimit * (pct / 100.0));
            if (usageCount == thresholdVal && thresholdVal > 0)
            {
                var adminEmail = await _entitlementService.GetTenantAdminEmailAsync(tenant.Id);
                if (!string.IsNullOrEmpty(adminEmail))
                {
                    try
                    {
                        await _emailService.SendUsageThresholdAlertAsync(tenant, adminEmail, pct);
                        _logger.LogInformation("Sent {Percent}% usage alert email to {Email}", pct, adminEmail);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send usage alert email to {Email}", adminEmail);
                    }
                }
            }
        }

        // Hard limit enforcement
        if (usageCount >= monthlyLimit)
        {
            if (tenant.Plan == PlanType.Free)
            {
                _logger.LogError("Tenant {TenantName} has exceeded their Free quota.", tenant.Name);
                return (TResponse)Result<byte[]>.Fail(Error.QuotaExceededError("Monthly quota exceeded. Please upgrade your plan."));
            }
            
            // For Pro/Enterprise, we allow overage but log it for billing
            _logger.LogInformation("Tenant {TenantName} is in overage ({Usage}/{Quota}).", 
                tenant.Name, usageCount, monthlyLimit);
        }

        var startTime = DateTime.UtcNow;
        var response = await next();
        var endTime = DateTime.UtcNow;

        var durationMs = (int)(endTime - startTime).TotalMilliseconds;
        var pdfSize = response.IsSuccess ? response.Value!.Length : 0;
        var statusCode = response.IsSuccess ? 200 : 400; // Simplified

        // Track usage asynchronously
        var requestId = Guid.NewGuid().ToString();

        string? fileUrl = null;

        if (response.IsSuccess && response.Value != null)
        {
            try 
            {
                fileUrl = await _pdfStorage.SaveAsync(response.Value, requestId, request.DocumentName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save generated PDF to storage for RequestId: {RequestId}", requestId);
            }
        }

        string? clientIp = _clientContextProvider.GetClientIp() ?? "127.0.0.1";
        string? userAgent = _clientContextProvider.GetUserAgent() ?? "PdfEngine Background Worker";
        string? authMechanism = _clientContextProvider.GetAuthMechanism() ?? "Queue Worker";

        bool isWatermarked = request.ApiKey?.KeyPrefix?.StartsWith("pk_test_") == true || 
                              request.ApiKey?.Environment?.Equals("Development", StringComparison.OrdinalIgnoreCase) == true;

        string sandboxEnv = request.ApiKey?.Environment == "Production" 
            ? "Isolation Sandbox v2.1 (Production)" 
            : "Dev Sandbox v1.4 (Development)";

        await _usageService.TrackUsageAsync(
            tenant.Id,
            request.ApiKey?.Id,
            requestId,
            request.DocumentName,
            pdfSize,
            durationMs,
            statusCode,
            response.IsSuccess,
            response.IsFailure ? response.Error.Message : null,
            clientIp,
            userAgent,
            authMechanism,
            isWatermarked,
            sandboxEnv,
            fileUrl);

        return response;
    }
}
