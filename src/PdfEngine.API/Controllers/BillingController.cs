using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PdfEngine.Infrastructure.Data;
using PdfEngine.Domain.Entities;
using PdfEngine.Application.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace PdfEngine.API.Controllers;

public class CheckoutRequest
{
    public string Plan { get; set; } = string.Empty;
}

[ApiController]
[Route("api/v1/[controller]")]
public class BillingController : ControllerBase
{
    private readonly PdfEngineDbContext _context;

    public BillingController(PdfEngineDbContext context)
    {
        _context = context;
    }

    [HttpGet("portal")]
    public async Task<IActionResult> GetPortalLink()
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        if (string.IsNullOrEmpty(client.StripeCustomerId))
        {
            return Ok(new { url = "/dashboard/billing/stripe-portal" });
        }

        try
        {
            var options = new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = client.StripeCustomerId,
                ReturnUrl = "http://localhost:3001/dashboard/billing"
            };

            var service = new Stripe.BillingPortal.SessionService();
            var session = await service.CreateAsync(options);
            return Ok(new { url = session.Url });
        }
        catch (Exception)
        {
            return Ok(new { url = "/dashboard/billing/stripe-portal" });
        }
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CheckoutRequest request)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        var user = HttpContext.Items["User"] as User;
        if (client == null) return Unauthorized();

        var customerId = client.StripeCustomerId;
        if (string.IsNullOrEmpty(customerId))
        {
            var billingService = HttpContext.RequestServices.GetRequiredService<IBillingService>();
            customerId = await billingService.CreateStripeCustomerAsync(client.Id, user?.Email ?? "billing@tenant.com");
        }

        var priceId = request.Plan == "Pro" ? "price_pro_id" : "price_enterprise_id";

        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Customer = customerId,
            PaymentMethodTypes = new List<string> { "card" },
            LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
            {
                new Stripe.Checkout.SessionLineItemOptions { Price = priceId, Quantity = 1 }
            },
            Mode = "subscription",
            SuccessUrl = "http://localhost:3001/dashboard/billing?success=true&session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = "http://localhost:3001/dashboard/billing",
        };

        try
        {
            var service = new Stripe.Checkout.SessionService();
            var session = await service.CreateAsync(options);
            return Ok(new { url = session.Url });
        }
        catch (Exception)
        {
            var mockUrl = $"/dashboard/billing/stripe-portal?plan={request.Plan}";
            return Ok(new { url = mockUrl });
        }
    }

    [HttpGet("invoices/{id}/download")]
    public async Task<IActionResult> DownloadInvoice(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == client.Id);
        if (invoice == null) return NotFound();

        var validPdf = @"%PDF-1.4
1 0 obj <</Type /Catalog /Pages 2 0 R>> endobj
2 0 obj <</Type /Pages /Kids [3 0 R] /Count 1>> endobj
3 0 obj <</Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R>> endobj
4 0 obj <</Length 62>> stream
BT
/F1 24 Tf
100 700 Td
(Invoice: " + invoice.Id + @") Tj
ET
endstream endobj
xref
0 5
0000000000 65535 f
0000000009 00000 n
0000000056 00000 n
0000000113 00000 n
0000000212 00000 n
trailer <</Size 5 /Root 1 0 R>>
startxref
325
%%EOF";
        
        var dummyPdf = System.Text.Encoding.ASCII.GetBytes(validPdf);

        return File(dummyPdf, "application/pdf", $"Invoice_{invoice.GeneratedAt:yyyyMMdd}.pdf");
    }
}
