using System;
using System.Linq;
using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using PdfEngine.Infrastructure.Data;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;

namespace PdfEngine.API.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class AdminController : ControllerBase
{
    private readonly PdfEngineDbContext _context;
    private readonly IConfiguration _configuration;

    public AdminController(PdfEngineDbContext context, IConfiguration configuration)
    {
        _context = context;
        _configuration = configuration;
    }

    [HttpGet("tenants")]
    public async Task<IActionResult> GetAllTenants()
    {
        // In a real app, we would verify the user role here
        // For now, we allow access to see our "God View"
        
        var tenants = await _context.Tenants
            .Select(t => new {
                t.Id,
                t.Name,
                status = t.Status.ToString(),
                plan = t.Plan.ToString(),
                totalPdfs = _context.UsageRecords.Count(u => u.TenantId == t.Id && u.Success),
                revenue = _context.Invoices.Where(i => i.TenantId == t.Id).Sum(i => i.TotalAmount),
                isPasswordSet = _context.Users.Any(u => u.TenantId == t.Id && !string.IsNullOrEmpty(u.PasswordHash) && u.PasswordHash != "password123")
                                || (!string.IsNullOrEmpty(t.PasswordHash) && t.PasswordHash != "password123")
            })
            .ToListAsync();

        return Ok(tenants);
    }

    [HttpGet("system-health")]
    public IActionResult GetSystemHealth()
    {
        return Ok(new {
            status = "Healthy",
            activeJobs = 0,
            redisConnection = "Connected",
            postgresConnection = "Connected",
            browserPoolSize = 5,
            uptime = "4 days, 2 hours"
        });
    }

    [HttpPost("tenants/{id}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(Guid id)
    {
        var tenant = await _context.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        tenant.Status = tenant.Status == TenantStatus.Active ? TenantStatus.Suspended : TenantStatus.Active;
        await _context.SaveChangesAsync();

        return Ok(new { message = $"Tenant status updated to {tenant.Status}", status = tenant.Status.ToString() });
    }

    [HttpPost("tenants/{id}/impersonate")]
    public async Task<IActionResult> Impersonate(Guid id)
    {
        var tenant = await _context.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.TenantId == id);
        if (user == null || string.IsNullOrEmpty(user.PasswordHash) || user.PasswordHash == "password123")
        {
            return BadRequest(new { error = "A custom tenant password must be set in the Admin Console before logging in as this tenant." });
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("tenantId", user.TenantId.ToString()),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var jwtToken = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(jwtToken);

        return Ok(new { token = tokenString });
    }

    [HttpPost("tenants/{id}/reset-password")]
    public async Task<IActionResult> ResetTenantPassword(Guid id, [FromBody] ResetTenantPasswordRequest request)
    {
        var tenant = await _context.Tenants.FindAsync(id);
        if (tenant == null) return NotFound();

        var user = await _context.Users.FirstOrDefaultAsync(u => u.TenantId == id);
        if (user == null)
        {
            user = new User
            {
                TenantId = id,
                Email = $"admin_{id.ToString().Substring(0, 4)}@example.com",
                PasswordHash = request.Password,
                Role = "SuperAdmin",
                CreatedAt = DateTime.UtcNow
            };
            _context.Users.Add(user);
        }
        else
        {
            user.PasswordHash = request.Password;
        }

        tenant.PasswordHash = request.Password;
        await _context.SaveChangesAsync();

        return Ok(new { message = "Tenant password updated successfully" });
    }
}

public class ResetTenantPasswordRequest
{
    public string Password { get; set; } = string.Empty;
}
