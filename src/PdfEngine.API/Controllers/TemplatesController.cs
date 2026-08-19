using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfEngine.Infrastructure.Data;
using PdfEngine.Domain.Entities;

namespace PdfEngine.API.Controllers;

public class UpdateTemplateRequest
{
    public string? HtmlContent { get; set; }
    public string? Description { get; set; }
}

[ApiController]
[Route("api/v1/[controller]")]
public class TemplatesController : ControllerBase
{
    private readonly PdfEngineDbContext _context;
    private readonly PdfEngine.Application.Interfaces.IEnvironmentProvider _environmentProvider;

    public TemplatesController(PdfEngineDbContext context, PdfEngine.Application.Interfaces.IEnvironmentProvider environmentProvider)
    {
        _context = context;
        _environmentProvider = environmentProvider;
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
            Environment = _environmentProvider.ActiveEnvironment,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.SavedTemplates.Add(template);
        await _context.SaveChangesAsync();

        var firstVersion = new SavedTemplateVersion
        {
            SavedTemplateId = template.Id,
            VersionNumber = 1,
            HtmlContent = template.HtmlContent,
            Description = "Initial Version",
            CreatedAt = DateTime.UtcNow
        };
        _context.SavedTemplateVersions.Add(firstVersion);
        await _context.SaveChangesAsync();

        return Ok(template);
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> UpdateTemplate(Guid id, [FromBody] UpdateTemplateRequest request)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .Include(t => t.Versions)
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        template.HtmlContent = request.HtmlContent ?? string.Empty;
        template.UpdatedAt = DateTime.UtcNow;

        var latestVersionNumber = template.Versions.Any() 
            ? template.Versions.Max(v => v.VersionNumber) 
            : 0;

        var newVersion = new SavedTemplateVersion
        {
            SavedTemplateId = template.Id,
            VersionNumber = latestVersionNumber + 1,
            HtmlContent = template.HtmlContent,
            Description = request.Description ?? $"Updated to version {latestVersionNumber + 1}",
            CreatedAt = DateTime.UtcNow
        };

        _context.SavedTemplateVersions.Add(newVersion);
        await _context.SaveChangesAsync();

        return Ok(template);
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> ListTemplateVersions(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var templateExists = await _context.SavedTemplates
            .AnyAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (!templateExists) return NotFound();

        var versions = await _context.SavedTemplateVersions
            .Where(v => v.SavedTemplateId == id)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new {
                id = v.Id,
                versionNumber = v.VersionNumber,
                description = v.Description,
                htmlContent = v.HtmlContent,
                createdAt = v.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            })
            .ToListAsync();

        return Ok(versions);
    }

    [HttpPost("{id}/rollback/{versionId}")]
    public async Task<IActionResult> RollbackTemplate(Guid id, Guid versionId)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        var version = await _context.SavedTemplateVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.SavedTemplateId == id);

        if (version == null) return NotFound();

        var latestVersionNumber = await _context.SavedTemplateVersions
            .Where(v => v.SavedTemplateId == id)
            .MaxAsync(v => (int?)v.VersionNumber) ?? 0;

        template.HtmlContent = version.HtmlContent;
        template.UpdatedAt = DateTime.UtcNow;

        var rollbackVersion = new SavedTemplateVersion
        {
            SavedTemplateId = template.Id,
            VersionNumber = latestVersionNumber + 1,
            HtmlContent = version.HtmlContent,
            Description = $"Rolled back to version {version.VersionNumber}",
            CreatedAt = DateTime.UtcNow
        };

        _context.SavedTemplateVersions.Add(rollbackVersion);
        await _context.SaveChangesAsync();

        return Ok(template);
    }

    [HttpPost("{id}/clone")]
    public async Task<IActionResult> CloneTemplate(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        var clonedTemplate = new SavedTemplate
        {
            TenantId = client.Id,
            Name = $"{template.Name} (Copy)",
            HtmlContent = template.HtmlContent,
            Environment = _environmentProvider.ActiveEnvironment,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.SavedTemplates.Add(clonedTemplate);
        await _context.SaveChangesAsync();

        var firstVersion = new SavedTemplateVersion
        {
            SavedTemplateId = clonedTemplate.Id,
            VersionNumber = 1,
            HtmlContent = clonedTemplate.HtmlContent,
            Description = $"Cloned from {template.Name}",
            CreatedAt = DateTime.UtcNow
        };
        _context.SavedTemplateVersions.Add(firstVersion);
        await _context.SaveChangesAsync();

        return Ok(clonedTemplate);
    }

    [HttpPost("{id}/fork")]
    public async Task<IActionResult> ForkTemplate(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        var forkedTemplate = new SavedTemplate
        {
            TenantId = client.Id,
            Name = $"{template.Name} (Forked)",
            HtmlContent = template.HtmlContent,
            Environment = _environmentProvider.ActiveEnvironment,
            ForkedFromId = template.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.SavedTemplates.Add(forkedTemplate);
        await _context.SaveChangesAsync();

        var firstVersion = new SavedTemplateVersion
        {
            SavedTemplateId = forkedTemplate.Id,
            VersionNumber = 1,
            HtmlContent = forkedTemplate.HtmlContent,
            Description = $"Forked from {template.Name}",
            CreatedAt = DateTime.UtcNow
        };
        _context.SavedTemplateVersions.Add(firstVersion);
        await _context.SaveChangesAsync();

        return Ok(forkedTemplate);
    }

    [HttpPost("{id}/publish/{versionId}")]
    public async Task<IActionResult> PublishTemplate(Guid id, Guid versionId)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        var version = await _context.SavedTemplateVersions
            .FirstOrDefaultAsync(v => v.Id == versionId && v.SavedTemplateId == id);

        if (version == null) return NotFound();

        var latestVersionNumber = await _context.SavedTemplateVersions
            .Where(v => v.SavedTemplateId == id)
            .MaxAsync(v => (int?)v.VersionNumber) ?? 0;

        template.HtmlContent = version.HtmlContent;
        template.UpdatedAt = DateTime.UtcNow;

        var publishVersion = new SavedTemplateVersion
        {
            SavedTemplateId = template.Id,
            VersionNumber = latestVersionNumber + 1,
            HtmlContent = version.HtmlContent,
            Description = $"Published version {version.VersionNumber} to production",
            CreatedAt = DateTime.UtcNow
        };

        _context.SavedTemplateVersions.Add(publishVersion);
        await _context.SaveChangesAsync();

        return Ok(template);
    }

    [HttpPost("{id}/archive")]
    public async Task<IActionResult> ArchiveTemplate(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        template.IsArchived = true;
        template.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(template);
    }

    [HttpPost("{id}/unarchive")]
    public async Task<IActionResult> UnarchiveTemplate(Guid id)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var template = await _context.SavedTemplates
            .FirstOrDefaultAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (template == null) return NotFound();

        template.IsArchived = false;
        template.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(template);
    }

    [HttpGet("{id}/compare/{versionIdA}/{versionIdB}")]
    public async Task<IActionResult> CompareTemplateVersions(Guid id, Guid versionIdA, Guid versionIdB)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        var templateExists = await _context.SavedTemplates
            .AnyAsync(t => t.Id == id && t.TenantId == client.Id && t.DeletedAt == null);

        if (!templateExists) return NotFound();

        var verA = await _context.SavedTemplateVersions
            .FirstOrDefaultAsync(v => v.Id == versionIdA && v.SavedTemplateId == id);

        var verB = await _context.SavedTemplateVersions
            .FirstOrDefaultAsync(v => v.Id == versionIdB && v.SavedTemplateId == id);

        if (verA == null || verB == null) return NotFound();

        var linesA = verA.HtmlContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var linesB = verB.HtmlContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var diff = new System.Collections.Generic.List<object>();
        int max = Math.Max(linesA.Length, linesB.Length);
        for (int i = 0; i < max; i++)
        {
            var lineA = i < linesA.Length ? linesA[i] : null;
            var lineB = i < linesB.Length ? linesB[i] : null;

            if (lineA == lineB)
            {
                diff.Add(new { type = "unchanged", content = lineA, lineNumber = i + 1 });
            }
            else
            {
                if (lineA != null)
                {
                    diff.Add(new { type = "removed", content = lineA, lineNumber = i + 1 });
                }
                if (lineB != null)
                {
                    diff.Add(new { type = "added", content = lineB, lineNumber = i + 1 });
                }
            }
        }

        return Ok(new
        {
            versionA = new { number = verA.VersionNumber, description = verA.Description, createdAt = verA.CreatedAt },
            versionB = new { number = verB.VersionNumber, description = verB.Description, createdAt = verB.CreatedAt },
            diff
        });
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

    [HttpPost("upload-bundle")]
    public async Task<IActionResult> UploadTemplatesBundle([FromForm] Microsoft.AspNetCore.Http.IFormFile file)
    {
        var client = HttpContext.Items["Client"] as Tenant;
        if (client == null) return Unauthorized();

        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No zip file was uploaded." });
        }

        using var stream = file.OpenReadStream();
        var validation = PdfEngine.Infrastructure.Security.ZipValidator.ValidateZipStream(stream);
        if (!validation.IsSafe)
        {
            return BadRequest(new { error = validation.ErrorMessage });
        }

        return Ok(new { success = true, message = "Templates bundle uploaded and verified successfully." });
    }
}

public class CreateTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? HtmlContent { get; set; }
}
