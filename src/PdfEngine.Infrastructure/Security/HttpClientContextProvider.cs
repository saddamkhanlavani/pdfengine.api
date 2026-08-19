using System;
using System.Linq;
using Microsoft.AspNetCore.Http;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Security;

public class HttpClientContextProvider : IClientContextProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpClientContextProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? GetClientIp()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) return null;

        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        if (httpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            ip = forwardedFor.ToString().Split(',').FirstOrDefault()?.Trim();
        }
        return ip;
    }

    public string? GetUserAgent()
    {
        return _httpContextAccessor.HttpContext?.Request.Headers["User-Agent"].ToString();
    }

    public string? GetAuthMechanism()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) return null;

        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            return "JWT / Session";
        }
        
        if (httpContext.Request.Headers.TryGetValue("X-API-KEY", out var apiKeyHeader))
        {
            var keyStr = apiKeyHeader.ToString();
            var prefix = keyStr.Length >= 8 ? keyStr.Substring(0, 8) : "";
            return $"API Key ({prefix}...)";
        }

        return "Anonymous";
    }
}
