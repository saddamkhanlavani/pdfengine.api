using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using PdfEngine.Infrastructure.Data;
using Stripe;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PdfEngine.API.Controllers;

[ApiController]
[Route("api/v1/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly IBillingService _billingService;
    private readonly PdfEngineDbContext _dbContext;
    private readonly ILogger<WebhooksController> _logger;
    private readonly string _webhookSecret;

    public WebhooksController(
        IBillingService billingService, 
        PdfEngineDbContext dbContext,
        IConfiguration configuration,
        ILogger<WebhooksController> _logger)
    {
        this._billingService = billingService;
        this._dbContext = dbContext;
        this._logger = _logger;
        this._webhookSecret = configuration["Stripe:WebhookSecret"] ?? "";
    }

    [HttpPost("stripe")]
    public async Task<IActionResult> HandleStripeWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();

        try
        {
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                _webhookSecret
            );

            if (_dbContext.ProcessedWebhookEvents.Any(e => e.Id == stripeEvent.Id))
            {
                _logger.LogInformation("Webhook event {EventId} already processed. Skipping.", stripeEvent.Id);
                return Ok();
            }

            switch (stripeEvent.Type)
            {
                case "invoice.paid":
                    var paidInvoice = stripeEvent.Data.Object as Stripe.Invoice;
                    await _billingService.HandlePaymentSuccessAsync(paidInvoice!.Id);
                    break;

                case "invoice.payment_failed":
                    var failedInvoice = stripeEvent.Data.Object as Stripe.Invoice;
                    await _billingService.HandlePaymentFailureAsync(failedInvoice!.Id);
                    break;

                case "customer.subscription.updated":
                    var subscription = stripeEvent.Data.Object as Stripe.Subscription;
                    await _billingService.UpdateSubscriptionStatusAsync(subscription!.Id, subscription.Status);
                    break;

                case "customer.subscription.deleted":
                    var deletedSubscription = stripeEvent.Data.Object as Stripe.Subscription;
                    await _billingService.UpdateSubscriptionStatusAsync(deletedSubscription!.Id, "canceled");
                    break;
            }

            _dbContext.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { Id = stripeEvent.Id });
            await _dbContext.SaveChangesAsync();

            return Ok();
        }
        catch (StripeException e)
        {
            _logger.LogError(e, "Stripe webhook signature validation failed.");
            return BadRequest();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error processing Stripe webhook.");
            return StatusCode(500);
        }
    }

    [HttpGet]
    public async Task<IActionResult> ListEndpoints()
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var endpoints = await _dbContext.WebhookEndpoints
            .Where(e => e.TenantId == tenant.Id && e.DeletedAt == null)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        return Ok(endpoints);
    }

    [HttpPost]
    public async Task<IActionResult> CreateEndpoint([FromBody] CreateWebhookRequest request)
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        if (string.IsNullOrEmpty(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return BadRequest(new { error = "Valid absolute Url is required." });
        }

        var endpoint = new PdfEngine.Domain.Entities.WebhookEndpoint
        {
            TenantId = tenant.Id,
            Url = request.Url,
            Description = request.Description,
            Secret = "whsec_" + Guid.NewGuid().ToString("N").Substring(0, 16),
            Events = string.Join(",", request.Events ?? Array.Empty<string>()),
            Environment = request.Environment,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.WebhookEndpoints.Add(endpoint);
        await _dbContext.SaveChangesAsync();

        return CreatedAtAction(nameof(ListEndpoints), new { id = endpoint.Id }, endpoint);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteEndpoint(Guid id)
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var endpoint = await _dbContext.WebhookEndpoints
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenant.Id && e.DeletedAt == null);

        if (endpoint == null) return NotFound();

        endpoint.DeletedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("{id}/deliveries")]
    public async Task<IActionResult> GetDeliveries(Guid id)
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var endpointExists = await _dbContext.WebhookEndpoints
            .AnyAsync(e => e.Id == id && e.TenantId == tenant.Id && e.DeletedAt == null);

        if (!endpointExists) return NotFound();

        var deliveries = await _dbContext.WebhookDeliveries
            .Where(d => d.EndpointId == id)
            .OrderByDescending(d => d.Timestamp)
            .Take(100)
            .ToListAsync();

        return Ok(deliveries);
    }

    [HttpPost("deliveries/{id}/replay")]
    public async Task<IActionResult> ReplayDelivery(Guid id)
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var delivery = await _dbContext.WebhookDeliveries
            .Include(d => d.Endpoint)
            .FirstOrDefaultAsync(d => d.Id == id && d.Endpoint.TenantId == tenant.Id && d.Endpoint.DeletedAt == null);

        if (delivery == null) return NotFound();

        var webhookService = HttpContext.RequestServices.GetRequiredService<IWebhookService>();
        
        await webhookService.DispatchAsync(tenant.Id, delivery.Event, System.Text.Json.JsonSerializer.Deserialize<object>(delivery.Payload)!);

        return Ok(new { message = "Webhook delivery replayed successfully" });
    }

    [HttpPost("test/{id}")]
    public async Task<IActionResult> TestEndpoint(Guid id)
    {
        var tenant = HttpContext.Items["Client"] as Tenant;
        if (tenant == null) return Unauthorized();

        var endpoint = await _dbContext.WebhookEndpoints
            .FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenant.Id && e.DeletedAt == null);

        if (endpoint == null) return NotFound();

        var webhookService = HttpContext.RequestServices.GetRequiredService<IWebhookService>();

        await webhookService.DispatchAsync(tenant.Id, "ping", new
        {
            message = "This is a test webhook request from PDFEngine.",
            endpointId = endpoint.Id,
            timestamp = DateTime.UtcNow
        });

        return Ok(new { message = "Test webhook dispatched successfully." });
    }
}

public class CreateWebhookRequest
{
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string[] Events { get; set; } = Array.Empty<string>();
    public string Environment { get; set; } = "Production";
}
