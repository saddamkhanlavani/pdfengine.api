using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PdfEngine.Application.Interfaces;
using PdfEngine.Infrastructure.Data;

namespace PdfEngine.API.Middlewares;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IApiKeyStore apiKeyStore)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/health") || 
            path.StartsWithSegments("/metrics") || 
            path.StartsWithSegments("/api/webhooks/stripe") || 
            path.StartsWithSegments("/api/v1/webhooks/stripe") || 
            path.StartsWithSegments("/api/auth") || 
            path.StartsWithSegments("/api/v1/auth") ||
            path.StartsWithSegments("/api/public") || 
            path.StartsWithSegments("/api/v1/public"))
        {
            await _next(context);
            return;
        }

        var dbContext = context.RequestServices.GetRequiredService<PdfEngineDbContext>();

        // Check if JWT Auth succeeded
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantIdClaim = context.User.FindFirst("tenantId")?.Value;
            var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            
            if (Guid.TryParse(tenantIdClaim, out var tenantId))
            {
                var jwtClient = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
                
                if (Guid.TryParse(userIdClaim, out var userId))
                {
                    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
                    if (user != null) context.Items["User"] = user;
                }
                
                if (jwtClient != null && jwtClient.IsActive)
                {
                    context.Items["Client"] = jwtClient;
                    context.Items["TenantId"] = jwtClient.Id; // Set active tenant context
                    await _next(context);
                    return;
                }
            }
        }

        // Check for Personal Access Token (PAT) in Authorization header
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader) && 
            !string.IsNullOrWhiteSpace(authHeader))
        {
            var authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer pat_", StringComparison.OrdinalIgnoreCase))
            {
                var patToken = authStr.Substring("Bearer ".Length).Trim();
                var patHash = PdfEngine.Infrastructure.Security.HashHelper.ComputeSha256Hash(patToken);

                var patRecord = await dbContext.PersonalAccessTokens
                    .Include(p => p.Tenant)
                    .Include(p => p.User)
                    .FirstOrDefaultAsync(p => p.TokenHash == patHash);

                if (patRecord != null && patRecord.Tenant != null && patRecord.Tenant.IsActive)
                {
                    patRecord.LastUsedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync();

                    context.Items["Client"] = patRecord.Tenant;
                    context.Items["TenantId"] = patRecord.TenantId;
                    context.Items["User"] = patRecord.User;

                    // Set claims identity so context.User.Identity?.IsAuthenticated yields true
                    var claims = new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, patRecord.UserId.ToString()),
                        new Claim("tenantId", patRecord.TenantId.ToString()),
                        new Claim(ClaimTypes.Role, patRecord.User?.Role ?? "Developer")
                    };
                    var identity = new ClaimsIdentity(claims, "PAT");
                    context.User = new ClaimsPrincipal(identity);

                    await _next(context);
                    return;
                }
                else
                {
                    await WriteErrorResponse(context, 401, "Personal Access Token is invalid or expired.");
                    return;
                }
            }
        }

        // Otherwise check API Key Auth
        if (!context.Request.Headers.TryGetValue("X-API-KEY", out var extractedApiKey) || string.IsNullOrWhiteSpace(extractedApiKey))
        {
            await WriteErrorResponse(context, 401, "API Key was not provided.");
            return;
        }

        var rawKey = extractedApiKey.ToString();
        var keyRecord = await apiKeyStore.GetApiKeyAsync(rawKey);

        if (keyRecord == null)
        {
            await WriteErrorResponse(context, 401, "API Key is invalid or not found.");
            return;
        }

        var client = keyRecord.Tenant;
        if (client == null || !client.IsActive)
        {
            await WriteErrorResponse(context, 403, "Subscription is inactive or suspended.");
            return;
        }

        if (client.Status == PdfEngine.Domain.Enums.TenantStatus.PastDue)
        {
            context.Response.Headers["X-Billing-Warning"] = "Payment is past due. Please update your billing info.";
        }

        // 1. IP Whitelist Validation
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(keyRecord.IpWhitelist))
        {
            var allowedIps = keyRecord.IpWhitelist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!string.IsNullOrEmpty(clientIp) && !allowedIps.Contains(clientIp))
            {
                await WriteErrorResponse(context, 403, $"Forbidden: Client IP {clientIp} is not in the whitelist.");
                return;
            }
        }

        // 2. Scope Validation
        var scopes = keyRecord.Scopes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToArray() ?? Array.Empty<string>();
        var requestPath = context.Request.Path.Value ?? "";
        if (requestPath.Contains("/pdf", StringComparison.OrdinalIgnoreCase) || 
            requestPath.Contains("/jobs", StringComparison.OrdinalIgnoreCase))
        {
            if (!scopes.Contains("render") && !scopes.Contains("render:pdf"))
            {
                await WriteErrorResponse(context, 403, "Forbidden: API key does not have the 'Render' scope.");
                return;
            }
        }
        else if (requestPath.Contains("/templates", StringComparison.OrdinalIgnoreCase))
        {
            if (!scopes.Contains("templates"))
            {
                await WriteErrorResponse(context, 403, "Forbidden: API key does not have the 'Templates' scope.");
                return;
            }
        }
        else if (requestPath.Contains("/logs", StringComparison.OrdinalIgnoreCase) ||
                 requestPath.Contains("/storage", StringComparison.OrdinalIgnoreCase) ||
                 requestPath.Contains("/download", StringComparison.OrdinalIgnoreCase))
        {
            if (!scopes.Contains("storage") && !scopes.Contains("logs:read"))
            {
                await WriteErrorResponse(context, 403, "Forbidden: API key does not have the 'Storage' scope.");
                return;
            }
        }
        else if (requestPath.Contains("/billing", StringComparison.OrdinalIgnoreCase) ||
                 requestPath.Contains("/invoices", StringComparison.OrdinalIgnoreCase) ||
                 requestPath.Contains("/subscription", StringComparison.OrdinalIgnoreCase))
        {
            if (!scopes.Contains("billing"))
            {
                await WriteErrorResponse(context, 403, "Forbidden: API key does not have the 'Billing' scope.");
                return;
            }
        }
        else if (requestPath.Contains("/admin", StringComparison.OrdinalIgnoreCase) ||
                 requestPath.Contains("/tenant", StringComparison.OrdinalIgnoreCase) ||
                 requestPath.Contains("/team", StringComparison.OrdinalIgnoreCase))
        {
            if (!scopes.Contains("admin"))
            {
                await WriteErrorResponse(context, 403, "Forbidden: API key does not have the 'Admin' scope.");
                return;
            }
        }

        // 3. Update Audit parameters
        keyRecord.LastUsedAt = DateTime.UtcNow;
        keyRecord.LastUsedIp = clientIp;
        
        // Save database changes
        dbContext.Entry(keyRecord).State = EntityState.Modified;
        await dbContext.SaveChangesAsync();

        // Inject both for downstream use
        context.Items["Client"] = client;
        context.Items["TenantId"] = client.Id; // Set active tenant context
        context.Items["ApiKey"] = keyRecord;
        
        await _next(context);
    }

    private static async Task WriteErrorResponse(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var result = JsonSerializer.Serialize(new { error = message });
        await context.Response.WriteAsync(result);
    }
}
