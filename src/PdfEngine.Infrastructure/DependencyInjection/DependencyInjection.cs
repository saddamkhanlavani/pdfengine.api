using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PdfEngine.Application.Configurations;
using PdfEngine.Application.Interfaces;
using PdfEngine.Infrastructure.Interfaces;
using PdfEngine.Infrastructure.Services;
using PdfEngine.Infrastructure.Security;
using PdfEngine.Infrastructure.Workers;
using Stripe;

namespace PdfEngine.Infrastructure.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PdfEngineOptions>(configuration.GetSection(PdfEngineOptions.SectionName));
        
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

        // Database
        services.AddDbContext<PdfEngine.Infrastructure.Data.PdfEngineDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Register BrowserManager as a Singleton
        services.AddSingleton<IBrowserManager, BrowserManager>();
        services.AddScoped<IApiKeyStore, EfApiKeyStore>();
        services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();
        
        // Business Services
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddSingleton<IWebhookService, WebhookService>();
        services.AddScoped<IUsageService, UsageService>();
        services.AddScoped<IBillingService, PdfEngine.Infrastructure.Services.BillingService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddHostedService<BillingWorker>();

        // Stripe Configuration
        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];

        // Register the concrete PlaywrightPdfService
        services.AddScoped<IPdfService, PlaywrightPdfService>();
        
        // Phase 7.1 Redis Async Services
        var redisConnectionString = configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
        services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(sp => 
            StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "PdfEngine_";
        });

        services.AddSingleton<IPdfJobQueue, PdfEngine.Infrastructure.Queue.RedisPdfJobQueue>();
        services.AddSingleton<IPdfJobStorage, PdfEngine.Infrastructure.Storage.RedisPdfJobStorage>();

        // Phase 8 Cloud Storage (S3 / MinIO)
        var hasS3 = !string.IsNullOrEmpty(configuration["AWS:ServiceURL"]);
        if (hasS3)
        {
            var s3Config = new Amazon.S3.AmazonS3Config
            {
                ServiceURL = configuration["AWS:ServiceURL"],
                ForcePathStyle = configuration.GetValue<bool>("AWS:ForcePathStyle", true),
                UseHttp = configuration["AWS:ServiceURL"]?.StartsWith("http://") == true
            };
            
            var credentials = new Amazon.Runtime.BasicAWSCredentials(
                configuration["AWS:AccessKey"] ?? "minioadmin", 
                configuration["AWS:SecretKey"] ?? "minioadmin"
            );
            services.AddSingleton<Amazon.S3.IAmazonS3>(new Amazon.S3.AmazonS3Client(credentials, s3Config));
            
            var bucketName = configuration["AWS:BucketName"] ?? "pdf-storage";
            services.AddSingleton<IPdfStorage>(sp => new PdfEngine.Infrastructure.Storage.S3PdfStorage(
                sp.GetRequiredService<Amazon.S3.IAmazonS3>(), 
                bucketName));
        }
        else
        {
            services.AddSingleton<IPdfStorage, PdfEngine.Infrastructure.Storage.LocalDiskPdfStorage>();
        }
        
        return services;
    }
}
