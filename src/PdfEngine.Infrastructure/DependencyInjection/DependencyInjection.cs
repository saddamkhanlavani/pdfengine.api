using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // Since .NET 6 an unhandled exception in a BackgroundService stops the HOST by
        // default. That default is right for a worker process whose only job is the
        // worker; it is wrong here, where the background workers are secondary and the
        // API's primary job — synchronous rendering — needs none of them. Measured by
        // tests/chaos_gate.py: an unreachable dependency took the whole API down.
        //
        // Each worker also catches its own exceptions and backs off. This is the second
        // line: a path nobody guarded must degrade to "that worker stopped", never to
        // "the service stopped".
        services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

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
                UseHttp = configuration["AWS:ServiceURL"]?.StartsWith("http://") == true,
                // The AWS SDK defaults to a 100-second timeout with 4 retries, which is
                // sized for a transient network hiccup talking to a bucket that exists.
                // Against a bucket that is DOWN it means one request waits minutes.
                // Measured by tests/chaos_gate.py: with MinIO stopped a render that
                // normally takes 0.3s took 68.9 SECONDS and still succeeded — long past
                // any client's own timeout, while holding a tenant render slot the whole
                // time. Storage is not on the critical path for a synchronous render, so
                // it gets a short leash.
                Timeout = TimeSpan.FromSeconds(5),
                MaxErrorRetry = 1
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
