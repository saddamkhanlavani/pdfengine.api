using System;
using Microsoft.AspNetCore.Http;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Security;

public class HttpContextEnvironmentProvider : IEnvironmentProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextEnvironmentProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string ActiveEnvironment
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null) return "Production";

            // If request is made using API key, the API key itself determines the environment
            if (httpContext.Items.TryGetValue("ApiKey", out var apiKeyObj) && apiKeyObj is PdfEngine.Domain.Entities.ApiKey key)
            {
                return key.Environment;
            }

            // Otherwise check the X-Environment header
            if (httpContext.Request.Headers.TryGetValue("X-Environment", out var envHeader))
            {
                var val = envHeader.ToString();
                if (val == "Development" || val == "Production")
                {
                    return val;
                }
            }

            return "Production";
        }
    }
}
