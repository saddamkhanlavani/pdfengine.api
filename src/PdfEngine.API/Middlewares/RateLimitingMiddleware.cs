using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;
using System;
using System.Threading.Tasks;

namespace PdfEngine.API.Middlewares;

public class RateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDistributedCache _cache;

    public RateLimitingMiddleware(RequestDelegate next, IDistributedCache cache)
    {
        _next = next;
        _cache = cache;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // ApiKeyMiddleware sets context.Items["Client"] for every authenticated caller —
        // API key, PAT, and JWT alike. Rate limiting must apply to all of them; only a
        // request that never resolved a tenant (e.g. the health/metrics/auth allowlist
        // in ApiKeyMiddleware) should skip it.
        var client = context.Items["Client"] as Tenant;
        if (client == null)
        {
            await _next(context);
            return;
        }

        // DYNAMIC RATE LIMITING: Get limits from database TenantEntitlement or PlanRegistry
        var planConfig = PlanRegistry.Plans[client.Plan];
        var limit = planConfig.RequestsPerMinute;

        var entitlementService = context.RequestServices.GetRequiredService<ITenantEntitlementService>();
        var entitlement = await entitlementService.GetEntitlementAsync(client.Id);
        if (entitlement != null && entitlement.RequestsPerMinute > 0)
        {
            limit = entitlement.RequestsPerMinute;
        }
        
        var key = $"rl_{client.Id}_{DateTime.UtcNow:yyyyMMddHHmm}";
        var currentRequestsRaw = await _cache.GetStringAsync(key);
        int currentRequests = string.IsNullOrEmpty(currentRequestsRaw) ? 0 : int.Parse(currentRequestsRaw);

        Console.WriteLine($"[RateLimiting] Tenant: {client.Name}, Plan: {client.Plan}, Limit: {limit}, Current: {currentRequests}");

        if (currentRequests >= limit)
        {
            context.Response.StatusCode = 429; // Too Many Requests
            await context.Response.WriteAsync("Rate limit exceeded. Please upgrade your plan for higher limits.");
            return;
        }

        await _cache.SetStringAsync(key, (currentRequests + 1).ToString(), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
        });

        await _next(context);
    }
}
