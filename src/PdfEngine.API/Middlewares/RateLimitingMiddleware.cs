using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
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
        var client = context.Items["Client"] as Tenant;
        if (client == null || context.User.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // DYNAMIC RATE LIMITING: Get limits from PlanRegistry based on tenant's plan
        var planConfig = PlanRegistry.Plans[client.Plan];
        var limit = planConfig.RequestsPerMinute;
        
        var key = $"rl_{client.Id}_{DateTime.UtcNow:yyyyMMddHHmm}";
        var currentRequestsRaw = await _cache.GetStringAsync(key);
        int currentRequests = string.IsNullOrEmpty(currentRequestsRaw) ? 0 : int.Parse(currentRequestsRaw);

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
