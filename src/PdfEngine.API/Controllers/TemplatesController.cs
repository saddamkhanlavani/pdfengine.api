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
public class TemplatesController : ControllerBase
{
    private readonly PdfEngineDbContext _context;

    public TemplatesController(PdfEngineDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> ListTemplates()
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var templates = await _context.SavedTemplates
            .Where(t => t.TenantId == client.Id && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync();

        return Ok(templates);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateTemplateRequest request)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Template name is required." });
        }

        var template = new SavedTemplate
        {
            TenantId = client.Id,
            Name = request.Name,
            HtmlContent = request.HtmlContent ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.SavedTemplates.Add(template);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(ListTemplates), new { id = template.Id }, template);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTemplate(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        template.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return NoContent();
    }
}

public class CreateTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? HtmlContent { get; set; }
}
