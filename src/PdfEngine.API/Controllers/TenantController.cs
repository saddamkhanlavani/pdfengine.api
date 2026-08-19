using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using System;
using System.IO;
using System.Threading.Tasks;

namespace PdfEngine.API.Controllers;

public class UpgradePlanRequest
{
    public string PriceId { get; set; } = string.Empty;
}

public class StorageSettingsRequest
{
    public string ProviderType { get; set; } = "S3"; // S3, MinIO, AzureBlob, GCS
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
}

public class OrgSettingsRequest
{
    public string BrandingColor { get; set; } = "#3b82f6";
    public string Locale { get; set; } = "en-US";
    public string Timezone { get; set; } = "UTC";
    public string? CustomLogoUrl { get; set; }
}

[ApiController]
[Route("api/v1/tenant")]
public class TenantController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;
    private readonly IBillingService _billingService;
    private readonly PdfEngine.Infrastructure.Data.PdfEngineDbContext _context;
    private readonly IEncryptionService _encryptionService;
    private readonly IEnvironmentProvider _environmentProvider;

    public TenantController(
        IApiKeyService apiKeyService, 
        IBillingService billingService,
        PdfEngine.Infrastructure.Data.PdfEngineDbContext context,
        IEncryptionService encryptionService,
        IEnvironmentProvider environmentProvider)
    {
        _apiKeyService = apiKeyService;
        _billingService = billingService;
        _context = context;
        _encryptionService = encryptionService;
        _environmentProvider = environmentProvider;
    }

    private bool IsDeveloper()
    {
        var user = HttpContext.Items["User"] as User;
        return user != null && user.Role == "Developer";
    }

    [HttpPost("keys/rotate")]
    public async Task<IActionResult> RotateKey()
    {
        if (IsDeveloper()) return Forbid("Developer role is restricted from API key modifications.");

        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var newKey = await _apiKeyService.RotateApiKeyAsync(tenant.Id);
        return Ok(new { ApiKey = newKey });
    }

    [HttpGet("billing/status")]
    public async Task<IActionResult> GetBillingStatus()
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        await Task.CompletedTask;
        return Ok(new
        {
            Status = tenant.Status.ToString(),
            Plan = tenant.Plan.ToString(),
            BillingCycleStart = tenant.BillingCycleStart
        });
    }

    [HttpPost("billing/upgrade")]
    public async Task<IActionResult> UpgradePlan([FromBody] UpgradePlanRequest request)
    {
        if (IsDeveloper()) return Forbid("Developer role is restricted from billing configurations.");

        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        await _billingService.UpgradePlanAsync(tenant.Id, request.PriceId);
        return Ok(new { Message = "Plan upgrade initiated." });
    }

    [HttpPost("storage/test")]
    public async Task<IActionResult> TestStorageConnection([FromBody] StorageSettingsRequest request)
    {
        if (IsDeveloper()) return Forbid("Developer role is restricted from modifying storage configurations.");

        try
        {
            if (request.ProviderType == "S3" || request.ProviderType == "MinIO" || request.ProviderType == "GCS")
            {
                var s3Config = new Amazon.S3.AmazonS3Config
                {
                    ForcePathStyle = true
                };
                if (!string.IsNullOrEmpty(request.Endpoint))
                {
                    s3Config.ServiceURL = request.Endpoint;
                    s3Config.UseHttp = request.Endpoint.StartsWith("http://");
                }
                else if (!string.IsNullOrEmpty(request.Region))
                {
                    s3Config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(request.Region);
                }

                var credentials = new Amazon.Runtime.BasicAWSCredentials(request.AccessKey, request.SecretKey);
                using var client = new Amazon.S3.AmazonS3Client(credentials, s3Config);

                // Test PutObject
                var key = "test-byos.txt";
                var putRequest = new Amazon.S3.Model.PutObjectRequest
                {
                    BucketName = request.BucketName,
                    Key = key,
                    ContentBody = "PDFEngine BYOS Validation Test",
                    ContentType = "text/plain"
                };
                await client.PutObjectAsync(putRequest);

                // Test GetObject
                var getRequest = new Amazon.S3.Model.GetObjectRequest
                {
                    BucketName = request.BucketName,
                    Key = key
                };
                using var getResponse = await client.GetObjectAsync(getRequest);
                using var reader = new StreamReader(getResponse.ResponseStream);
                var content = await reader.ReadToEndAsync();
                if (content != "PDFEngine BYOS Validation Test")
                {
                    throw new Exception("Read test failed: Content mismatch.");
                }

                // Test DeleteObject
                var deleteRequest = new Amazon.S3.Model.DeleteObjectRequest
                {
                    BucketName = request.BucketName,
                    Key = key
                };
                await client.DeleteObjectAsync(deleteRequest);
            }
            else
            {
                // Simulated connection check for other storage providers
                await Task.Delay(500);
            }

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("storage")]
    public async Task<IActionResult> GetStorageSettings()
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var provider = await _context.StorageProviders
            .FirstOrDefaultAsync();

        if (provider == null)
        {
            return Ok(new
            {
                providerType = "None",
                bucketName = "",
                region = "",
                endpoint = "",
                accessKey = "",
                secretKey = "",
                isActive = false
            });
        }

        return Ok(new
        {
            providerType = provider.ProviderType,
            bucketName = provider.BucketName,
            region = provider.Region,
            endpoint = provider.Endpoint,
            accessKey = provider.AccessKeyEncrypted.Length > 8 ? "••••••••" : "",
            secretKey = "••••••••",
            isActive = provider.IsActive
        });
    }

    [HttpPost("storage")]
    public async Task<IActionResult> SaveStorageSettings([FromBody] StorageSettingsRequest request)
    {
        if (IsDeveloper()) return Forbid("Developer role is restricted from modifying storage configurations.");

        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        // Run connection test first
        var testResult = await TestStorageConnection(request);
        if (testResult is BadRequestObjectResult)
        {
            return testResult;
        }

        var provider = await _context.StorageProviders.FirstOrDefaultAsync();

        if (provider == null)
        {
            provider = new StorageProvider
            {
                TenantId = tenant.Id,
                Environment = _environmentProvider.ActiveEnvironment
            };
            _context.StorageProviders.Add(provider);
        }

        provider.ProviderType = request.ProviderType;
        provider.BucketName = request.BucketName;
        provider.Region = request.Region;
        provider.Endpoint = request.Endpoint;
        provider.AccessKeyEncrypted = _encryptionService.Encrypt(request.AccessKey);
        provider.SecretKeyEncrypted = _encryptionService.Encrypt(request.SecretKey);
        provider.IsActive = true;

        await _context.SaveChangesAsync();
        return Ok(new { message = "Storage configuration saved successfully." });
    }

    [HttpDelete("storage")]
    public async Task<IActionResult> DeleteStorageSettings()
    {
        if (IsDeveloper()) return Forbid("Developer role is restricted from modifying storage configurations.");

        var provider = await _context.StorageProviders.FirstOrDefaultAsync();
        if (provider == null) return NotFound();

        _context.StorageProviders.Remove(provider);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("org-settings")]
    public async Task<IActionResult> GetOrgSettings()
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        return Ok(new
        {
            brandingColor = tenant.BrandingColor,
            locale = tenant.Locale,
            timezone = tenant.Timezone,
            customLogoUrl = tenant.CustomLogoUrl
        });
    }

    [HttpPost("org-settings")]
    public async Task<IActionResult> SaveOrgSettings([FromBody] OrgSettingsRequest request)
    {
        if (IsDeveloper()) return Forbid("Developer role is restricted from modifying organization configurations.");

        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        tenant.BrandingColor = request.BrandingColor;
        tenant.Locale = request.Locale;
        tenant.Timezone = request.Timezone;
        tenant.CustomLogoUrl = request.CustomLogoUrl;

        _context.Entry(tenant).State = EntityState.Modified;
        await _context.SaveChangesAsync();

        return Ok(new { message = "Organization settings saved successfully." });
    }
}
