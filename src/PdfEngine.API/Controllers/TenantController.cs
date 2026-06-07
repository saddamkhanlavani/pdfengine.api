using Microsoft.AspNetCore.Mvc;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using System.Threading.Tasks;

namespace PdfEngine.API.Controllers;

[ApiController]
[Route("api/v1/tenant")]
public class TenantController : ControllerBase
{
    private readonly IApiKeyService _apiKeyService;
    private readonly IBillingService _billingService;

    public TenantController(IApiKeyService apiKeyService, IBillingService billingService)
    {
        _apiKeyService = apiKeyService;
        _billingService = billingService;
    }

    [HttpPost("keys/rotate")]
    public async Task<IActionResult> RotateKey()
    {
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
    public async Task<IActionResult> UpgradePlan([FromBody] string priceId)
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        await _billingService.UpgradePlanAsync(tenant.Id, priceId);
        return Ok(new { Message = "Plan upgrade initiated." });
    }
}
