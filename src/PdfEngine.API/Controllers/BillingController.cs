using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Infrastructure.Data;
using PdfEngine.Domain.Entities;

namespace PdfEngine.API.Controllers;

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
    public IActionResult GetPortalLink()
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        return Ok(new { url = "/dashboard/billing/stripe-portal" });
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
