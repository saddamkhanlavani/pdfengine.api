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

        // Everything the engine draws into a PDF itself — footnote bands, running headers,
        // watermarks — goes through PdfSharpCore, which needs a font resolver to have any
        // idea what a family or a weight means. Without this it was measured to return one
        // identical face for every family AND every style, so `font-family` in a margin box
        // did nothing and emphasis in a footnote rendered upright. Registered here because
        // it is process-wide state that must be in place before the first render.
        EngineFontResolver.Register();

        
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
        services.AddScoped<IEnvironmentProvider, HttpContextEnvironmentProvider>();
        services.AddScoped<IClientContextProvider, HttpClientContextProvider>();

        // Database
        services.AddDbContext<PdfEngine.Infrastructure.Data.PdfEngineDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Register BrowserManager as a Singleton
        services.AddSingleton<IBrowserManager, BrowserManager>();
        services.AddScoped<IApiKeyStore, EfApiKeyStore>();
        services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();
        
        // Email Configuration & Providers
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddTransient<LogEmailProvider>();
        services.AddTransient<SmtpEmailProvider>();
        services.AddTransient<SendGridEmailProvider>();
        services.AddTransient<PostmarkEmailProvider>();

        services.AddTransient<IEmailProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmailOptions>>().Value;
            return options.Provider.ToUpperInvariant() switch
            {
                "SMTP" => sp.GetRequiredService<SmtpEmailProvider>(),
                "SENDGRID" => sp.GetRequiredService<SendGridEmailProvider>(),
                "POSTMARK" => sp.GetRequiredService<PostmarkEmailProvider>(),
                _ => sp.GetRequiredService<LogEmailProvider>()
            };
        });
        services.AddScoped<IEmailService, EmailService>();
        
        // Business Services
        services.AddSingleton<IEncryptionService, EncryptionService>();
        services.AddSingleton<IWebhookService, WebhookService>();
        services.AddScoped<IUsageService, UsageService>();
        services.AddScoped<IBillingService, PdfEngine.Infrastructure.Services.BillingService>();
        services.AddScoped<IApiKeyService, ApiKeyService>();
        services.AddScoped<ITenantEntitlementService, TenantEntitlementService>();
        services.AddHostedService<BillingWorker>();
        services.AddHostedService<PdfRenderWorker>();
        services.AddHostedService<MetricsWorker>();

        // Stripe Configuration
        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];

        services.AddScoped<IHtmlSanitizerStage, HtmlSanitizerStage>();
        services.AddScoped<IAssetOptimizerStage, AssetOptimizerStage>();
        services.AddScoped<IDomAnalyzer, DomAnalyzer>();
        services.AddScoped<ILayoutAnalyzer, LayoutAnalyzer>();
        services.AddScoped<ITypographyEngine, TypographyEngine>();
        services.AddScoped<IPaginationPlanner, PaginationPlanner>();

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
