using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace PdfEngine.API.Middlewares;

public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Content Security Policy (CSP)
        context.Response.Headers["Content-Security-Policy"] = 
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdnjs.cloudflare.com; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdnjs.cloudflare.com; " +
            "font-src 'self' https://fonts.gstatic.com https://cdnjs.cloudflare.com data:; " +
            "img-src 'self' data: https:; " +
            "connect-src 'self' http://localhost:* ws://localhost:* https://*.stripe.com; " +
            "frame-ancestors 'self';";

        // Prevent Clickjacking
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";

        // Prevent MIME type sniffing
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // Referrer Policy
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Restrict Browser Features (Permissions Policy)
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), interest-cohort=()";

        // HSTS (Strict-Transport-Security) - only if secure connection
        if (context.Request.IsHttps)
        {
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";
        }

        await _next(context);
    }
}
