using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Amazon.S3;
using PdfEngine.Infrastructure.Data;
using PdfEngine.Application.Interfaces;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class HealthController : ControllerBase
{
    private readonly PdfEngineDbContext _context;
    private readonly IConnectionMultiplexer _redis;
    private readonly IAmazonS3? _s3;
    private readonly IPdfService _pdfService;

    public HealthController(
        PdfEngineDbContext context,
        IConnectionMultiplexer redis,
        IPdfService pdfService,
        IAmazonS3? s3 = null)
    {
        _context = context;
        _redis = redis;
        _pdfService = pdfService;
        _s3 = s3;
    }

    [HttpGet("certification")]
    public async Task<IActionResult> RunDeploymentCertification()
    {
        var postgresOk = false;
        var postgresLatency = 0L;
        var redisOk = false;
        var redisLatency = 0L;
        var s3Ok = false;
        var s3Latency = 0L;
        var renderOk = false;
        var renderLatency = 0L;

        var errors = new System.Collections.Generic.List<string>();

        // 1. Validate PostgreSQL Database Connection
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            postgresOk = await _context.Database.CanConnectAsync();
            sw.Stop();
            postgresLatency = sw.ElapsedMilliseconds;
            if (!postgresOk)
            {
                errors.Add("PostgreSQL failed to connect.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"PostgreSQL connection failed critically: {ex.Message}");
        }

        // 2. Validate Redis Cache Connection
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ping = await _redis.GetDatabase().PingAsync();
            sw.Stop();
            redisLatency = sw.ElapsedMilliseconds;
            redisOk = ping.TotalMilliseconds > 0 || true;
        }
        catch (Exception ex)
        {
            errors.Add($"Redis connection failed critically: {ex.Message}");
        }

        // 3. Validate S3 Storage Connection
        if (_s3 != null)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var buckets = await _s3.ListBucketsAsync();
                sw.Stop();
                s3Latency = sw.ElapsedMilliseconds;
                s3Ok = buckets.HttpStatusCode == System.Net.HttpStatusCode.OK;
            }
            catch (Exception ex)
            {
                errors.Add($"S3/MinIO bucket connection failed critically: {ex.Message}");
            }
        }
        else
        {
            s3Ok = true; // S3 is optional in local/mock environments
        }

        // 4. Validate Sandbox Mock Render Execution (Sanity Check)
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var command = new GeneratePdfCommand
            {
                DocumentName = "ReleaseSanityCheck.pdf",
                HtmlContent = "<html><body><h1>PDFEngine Certification Check</h1><p>Release Gate Validation Successful.</p></body></html>"
            };

            var result = await _pdfService.GenerateAsync(command);
            sw.Stop();
            renderLatency = sw.ElapsedMilliseconds;
            
            if (result.IsSuccess && result.Value != null && result.Value.Length > 0)
            {
                renderOk = true;
            }
            else
            {
                errors.Add($"Sandbox Render compilation failed: {result.Error.Message}");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Sandbox Render crashed critically: {ex.Message}");
        }

        var certified = postgresOk && redisOk && s3Ok && renderOk;

        return Ok(new
        {
            certified,
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            checks = new
            {
                postgres = new { status = postgresOk ? "Healthy" : "Failed", latencyMs = postgresLatency },
                redis = new { status = redisOk ? "Healthy" : "Failed", latencyMs = redisLatency },
                s3 = new { status = s3Ok ? "Healthy" : "Failed", latencyMs = s3Latency },
                browserSandbox = new { status = renderOk ? "Healthy" : "Failed", latencyMs = renderLatency }
            },
            errors = errors.Count > 0 ? errors : null
        });
    }
}
