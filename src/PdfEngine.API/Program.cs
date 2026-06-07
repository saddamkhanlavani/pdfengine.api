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

// PHASE 10: Configure Serilog for Structured Logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "PdfEngine.API")
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter())
    .CreateLogger();

builder.Host.UseSerilog();

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("DashboardPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:3001")
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
            ForcePathStyle = true
        };
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

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<RateLimitingMiddleware>();

app.MapControllers();

// PHASE 10: Health Check Endpoint
app.MapHealthChecks("/health");

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
        
        var adminTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var adminTenant = await context.Tenants.FindAsync(adminTenantId);
        if (adminTenant != null)
        {
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
        }
        
        await context.SaveChangesAsync();
        Log.Information("Completed API key auto-seeding verification.");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to seed default API keys on startup.");
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
