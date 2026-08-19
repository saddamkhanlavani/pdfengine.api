using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using PdfEngine.API.Middlewares;
using Serilog;
using Serilog.Events;
using Prometheus;
using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Prevent port conflict in development by killing any process on port 5276
if (builder.Environment.IsDevelopment())
{
    try
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "bash",
            Arguments = "-c \"lsof -t -i:5276 | xargs kill -9\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(startInfo);
        process?.WaitForExit(2000);
    }
    catch { /* Ignore failures in non-bash environments */ }
}


// PHASE 10: Configure Serilog for Structured Logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "PdfEngine.API")
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

builder.Host.UseSerilog();

// Validate configurations on startup
PdfEngine.Infrastructure.Configuration.StartupConfigValidator.Validate(builder.Configuration);

// Optional external tools. Not fatal — the engine renders without them — but an operator
// who sees this line at boot can install the package, whereas a caller who gets a 400
// mid-render cannot.
foreach (var finding in PdfEngine.Infrastructure.Configuration.StartupConfigValidator.CheckOptionalTools())
{
    Console.WriteLine($"[PdfEngine] optional tool: {finding}");
}

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:3000", "http://localhost:3001")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();

// Register layers
PdfEngine.Application.DependencyInjection.AddApplication(builder.Services);
PdfEngine.Infrastructure.DependencyInjection.DependencyInjection.AddInfrastructure(builder.Services, builder.Configuration);

// PHASE 10: Health Checks
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!)
    .AddRedis(builder.Configuration["Redis:ConnectionString"]!)
    .AddS3(options =>
    {
        options.S3Config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = builder.Configuration["AWS:ServiceURL"],
            ForcePathStyle = true,
            UseHttp = builder.Configuration["AWS:ServiceURL"]?.StartsWith("http://") == true
        };
        options.Credentials = new Amazon.Runtime.BasicAWSCredentials(
            builder.Configuration["AWS:AccessKey"] ?? "minioadmin",
            builder.Configuration["AWS:SecretKey"] ?? "minioadmin"
        );
        options.BucketName = builder.Configuration["AWS:BucketName"] ?? "pdf-storage";
    }, name: "S3 Storage");

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// PHASE 10: Prometheus Metrics
app.UseMetricServer(); // Exposes /metrics
app.UseHttpMetrics();  // Tracks default HTTP metrics (latency, count)

app.UseSerilogRequestLogging(); // Efficient request logging

app.UseCors("DashboardPolicy");

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();

app.MapControllers();

// PHASE 10: Health Check Endpoint
app.MapHealthChecks("/health");

// Custom Startup Diagnostics Health Endpoint
app.MapGet("/health/startup", async (
    PdfEngine.Infrastructure.Data.PdfEngineDbContext dbContext,
    StackExchange.Redis.IConnectionMultiplexer redis,
    PdfEngine.Infrastructure.Interfaces.IBrowserManager browserManager) =>
{
    var dbHealthy = false;
    var redisHealthy = false;
    var browserHealthy = false;
    
    var dbError = "";
    var redisError = "";
    var browserError = "";

    try
    {
        dbHealthy = await dbContext.Database.CanConnectAsync();
    }
    catch (Exception ex)
    {
        dbError = ex.Message;
    }

    try
    {
        redisHealthy = redis.IsConnected;
    }
    catch (Exception ex)
    {
        redisError = ex.Message;
    }

    try
    {
        browserHealthy = browserManager.IsBrowserAlive();
    }
    catch (Exception ex)
    {
        browserError = ex.Message;
    }

    var result = new
    {
        status = (dbHealthy && redisHealthy && browserHealthy) ? "Healthy" : "Unhealthy",
        timestamp = DateTime.UtcNow,
        checks = new[]
        {
            new { name = "Database", status = dbHealthy ? "Healthy" : "Unhealthy", error = dbError },
            new { name = "Redis", status = redisHealthy ? "Healthy" : "Unhealthy", error = redisError },
            new { name = "BrowserPool", status = browserHealthy ? "Healthy" : "Unhealthy", error = browserError }
        }
    };

    return (dbHealthy && redisHealthy && browserHealthy)
        ? Microsoft.AspNetCore.Http.Results.Ok(result)
        : Microsoft.AspNetCore.Http.Results.Json(result, statusCode: 503);
});

// PHASE 10: Auto-seeding API Keys
using (var scope = app.Services.CreateScope())
{
    try
    {
        var context = scope.ServiceProvider.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        var tenants = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(context.Tenants);
        foreach (var tenant in tenants)
        {
            var hasKeys = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(context.ApiKeys.IgnoreQueryFilters(), k => k.TenantId == tenant.Id);
            if (!hasKeys)
            {
                var keyStr = "pk_live_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                var defaultKey = new PdfEngine.Domain.Entities.ApiKey
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    Key = keyStr,
                    KeyPrefix = "pk_live_",
                    KeyHash = PdfEngine.Infrastructure.Security.HashHelper.ComputeSha256Hash(keyStr),
                    Environment = "Production",
                    Scopes = "render:pdf,logs:read",
                    CreatedAt = DateTime.UtcNow
                };
                context.ApiKeys.Add(defaultKey);
                
                var testKeyStr = "pk_test_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                var defaultTestKey = new PdfEngine.Domain.Entities.ApiKey
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    Key = testKeyStr,
                    KeyPrefix = "pk_test_",
                    KeyHash = PdfEngine.Infrastructure.Security.HashHelper.ComputeSha256Hash(testKeyStr),
                    Environment = "Development",
                    Scopes = "render:pdf,logs:read",
                    CreatedAt = DateTime.UtcNow
                };
                context.ApiKeys.Add(defaultTestKey);
            }
        }
        
        // Seeded admin account + fixed test API key are local-dev conveniences only.
        // They must never be created against a shared/production database — a
        // well-known email/password/key combo seeded unconditionally is a standing
        // backdoor if this code path ever runs outside development.
        if (app.Environment.IsDevelopment())
        {
            var adminTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var adminTenant = await context.Tenants.FindAsync(adminTenantId);
            if (adminTenant == null)
            {
                adminTenant = new PdfEngine.Domain.Entities.Tenant
                {
                    Id = adminTenantId,
                    Name = "Test Admin",
                    Plan = PdfEngine.Domain.Enums.PlanType.Enterprise,
                    Status = PdfEngine.Domain.Enums.TenantStatus.Active,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123")
                };
                context.Tenants.Add(adminTenant);
                await context.SaveChangesAsync();
            }
            else if (adminTenant.Plan != PdfEngine.Domain.Enums.PlanType.Enterprise || string.IsNullOrEmpty(adminTenant.PasswordHash) || !adminTenant.PasswordHash.StartsWith("$2"))
            {
                adminTenant.Plan = PdfEngine.Domain.Enums.PlanType.Enterprise;
                adminTenant.PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123");
                await context.SaveChangesAsync();
            }

            var adminUser = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(context.Users.IgnoreQueryFilters(), u => u.Email == "admin@example.com");
            if (adminUser == null)
            {
                adminUser = new PdfEngine.Domain.Entities.User
                {
                    Id = Guid.NewGuid(),
                    TenantId = adminTenantId,
                    Email = "admin@example.com",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
                    Role = "SuperAdmin",
                    CreatedAt = DateTime.UtcNow
                };
                context.Users.Add(adminUser);
                await context.SaveChangesAsync();
            }
            else if (adminUser.Role != "SuperAdmin" || string.IsNullOrEmpty(adminUser.PasswordHash) || !adminUser.PasswordHash.StartsWith("$2"))
            {
                adminUser.Role = "SuperAdmin";
                adminUser.PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123");
                await context.SaveChangesAsync();
            }

            var existingKey = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(context.ApiKeys.IgnoreQueryFilters(), k => k.Key == "test-api-key-123");
            if (existingKey == null)
            {
                var testKey = new PdfEngine.Domain.Entities.ApiKey
                {
                    Id = Guid.NewGuid(),
                    TenantId = adminTenantId,
                    Key = "test-api-key-123",
                    KeyPrefix = "pk_live_",
                    KeyHash = PdfEngine.Infrastructure.Security.HashHelper.ComputeSha256Hash("test-api-key-123"),
                    Environment = "Production",
                    Scopes = "render:pdf,logs:read",
                    CreatedAt = DateTime.UtcNow
                };
                context.ApiKeys.Add(testKey);
            }

            // SECOND, fully separate dev tenant. Exists so multi-tenant ISOLATION can
            // actually be tested: with only one seeded tenant there is no second
            // identity to prove separation against, and an isolation test would be
            // vacuous. Same development-only gate as everything above.
            var otherTenantId = Guid.Parse("00000000-0000-0000-0000-000000000002");
            var otherTenant = await context.Tenants.FindAsync(otherTenantId);
            if (otherTenant == null)
            {
                otherTenant = new PdfEngine.Domain.Entities.Tenant
                {
                    Id = otherTenantId,
                    Name = "Test Tenant B",
                    Plan = PdfEngine.Domain.Enums.PlanType.Startup,
                    Status = PdfEngine.Domain.Enums.TenantStatus.Active,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123")
                };
                context.Tenants.Add(otherTenant);
                await context.SaveChangesAsync();
            }

            var otherKey = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(context.ApiKeys.IgnoreQueryFilters(), k => k.Key == "test-api-key-tenant-b");
            if (otherKey == null)
            {
                context.ApiKeys.Add(new PdfEngine.Domain.Entities.ApiKey
                {
                    Id = Guid.NewGuid(),
                    TenantId = otherTenantId,
                    Key = "test-api-key-tenant-b",
                    KeyPrefix = "pk_live_",
                    KeyHash = PdfEngine.Infrastructure.Security.HashHelper.ComputeSha256Hash("test-api-key-tenant-b"),
                    Environment = "Production",
                    Scopes = "render:pdf,logs:read",
                    CreatedAt = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync();
            Log.Information("Completed dev-only admin/test-key seeding (Development environment).");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to seed default API keys or admin user on startup.");
    }

}

try
{
    Log.Information("Starting PdfEngine API...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
