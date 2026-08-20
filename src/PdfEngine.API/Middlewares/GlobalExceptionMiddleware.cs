using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PdfEngine.Application.Common;
using PdfEngine.Application.Configurations;
using PdfEngine.API.Contracts;

namespace PdfEngine.API.Middlewares;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly PdfEngineOptions _options;

    public GlobalExceptionMiddleware(
        RequestDelegate next, 
        ILogger<GlobalExceptionMiddleware> logger,
        IOptions<PdfEngineOptions> options)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            if (IsDependencyUnavailable(ex))
            {
                // Logged as an error, not as critical: a dependency outage is not a defect
                // in this service, and paging on it trains people to ignore the page.
                _logger.LogError(ex, "A backing service was unavailable during request processing.");
            }
            else
            {
                _logger.LogCritical(ex, "A critical unhandled exception occurred during request processing.");
            }
            await HandleExceptionAsync(context, ex);
        }
    }

    /// <summary>
    /// Whether this exception means "a backing service is unreachable" rather than "the
    /// engine is broken". The distinction is the caller's, not ours: a 500 is a bug report
    /// and gets a human out of bed, a 503 with Retry-After is a retry the client already
    /// knows how to do. Measured by tests/chaos_gate.py — stopping Redis or Postgres
    /// surfaced as INTERNAL_ERROR, which is how a ten-second dependency blip becomes an
    /// incident.
    ///
    /// Matched on type NAME so this layer needs no reference to StackExchange.Redis or
    /// Npgsql; the inner chain is walked because these arrive wrapped.
    /// </summary>
    private static bool IsDependencyUnavailable(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException!)
        {
            var name = ex.GetType().Name;
            if (name is "RedisConnectionException" or "RedisTimeoutException"
                     or "NpgsqlException" or "PostgresException"
                     or "AmazonS3Exception" or "HttpRequestException"
                     or "SocketException")
            {
                return true;
            }
            // Npgsql wraps host-resolution failures in a plain DbException whose message is
            // the resolver's; "Name or service not known" is what a stopped container looks
            // like from inside the API.
            if (ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("no connection became available", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (ex.InnerException is null) break;
        }
        return false;
    }

    private Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var dependencyDown = IsDependencyUnavailable(exception);
        context.Response.StatusCode = dependencyDown
            ? (int)HttpStatusCode.ServiceUnavailable
            : (int)HttpStatusCode.InternalServerError;
        if (dependencyDown)
        {
            context.Response.Headers["Retry-After"] = "10";
        }

        var message = _options.EnableDetailedErrors 
            ? exception.Message 
            : dependencyDown
                ? "A service this request depends on is temporarily unavailable. Retry shortly."
                : "An internal server error occurred while processing your request.";

        var response = new ErrorResponse
        {
            Code = dependencyDown ? ErrorCodes.DependencyUnavailable : ErrorCodes.Internal,
            Message = message,
            TraceId = context.TraceIdentifier
        };

        var result = JsonSerializer.Serialize(response, new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        });

        return context.Response.WriteAsync(result);
    }
}
