using Microsoft.EntityFrameworkCore;
using PdfEngine.Domain.Entities;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Data;

public class PdfEngineDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;
    private readonly IEnvironmentProvider _environmentProvider;

    public PdfEngineDbContext(
        DbContextOptions<PdfEngineDbContext> options,
        ITenantProvider tenantProvider,
        IEnvironmentProvider environmentProvider) : base(options)
    {
        _tenantProvider = tenantProvider;
        _environmentProvider = environmentProvider;
    }

    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<ApiKey> ApiKeys { get; set; }
    public DbSet<UsageRecord> UsageRecords { get; set; }
    public DbSet<Invoice> Invoices { get; set; }
    public DbSet<AuditLog> AuditLogs { get; set; }
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; set; }
    
    // New SaaS Platform Entities
    public DbSet<TwoFactorRecoveryCode> TwoFactorRecoveryCodes { get; set; }
    public DbSet<RefreshToken> RefreshTokens { get; set; }
    public DbSet<WebhookEndpoint> WebhookEndpoints { get; set; }
    public DbSet<WebhookDelivery> WebhookDeliveries { get; set; }
    public DbSet<SavedTemplate> SavedTemplates { get; set; }
    public DbSet<SavedTemplateVersion> SavedTemplateVersions { get; set; }
    public DbSet<DownloadAccessLog> DownloadAccessLogs { get; set; }
    public DbSet<PdfJob> PdfJobs { get; set; }
    public DbSet<StorageProvider> StorageProviders { get; set; }
    public DbSet<SSOConfiguration> SSOConfigurations { get; set; }
    public DbSet<SCIMProvisioning> SCIMProvisionings { get; set; }
    public DbSet<BrowserWorkerMetrics> BrowserWorkerMetrics { get; set; }
    public DbSet<FeatureFlag> FeatureFlags { get; set; }
    public DbSet<Notification> Notifications { get; set; }
    public DbSet<SdkUsage> SdkUsages { get; set; }
    public DbSet<RenderAsset> RenderAssets { get; set; }
    public DbSet<PdfJobSnapshot> PdfJobSnapshots { get; set; }

    // Production SaaS entities
    public DbSet<TenantEntitlement> TenantEntitlements { get; set; }
    public DbSet<Invitation> Invitations { get; set; }
    public DbSet<UsageAggregate> UsageAggregates { get; set; }
    public DbSet<EmailLog> EmailLogs { get; set; }
    public DbSet<Asset> Assets { get; set; }
    public DbSet<SupportTicket> SupportTickets { get; set; }
    public DbSet<PersonalAccessToken> PersonalAccessTokens { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Primary key configuration
        modelBuilder.Entity<PdfJob>().HasKey(j => j.JobId);
        modelBuilder.Entity<PdfJobSnapshot>().HasKey(s => s.JobId);

        // Idempotent Invoices: prevent double-billing for the same period
        modelBuilder.Entity<Invoice>()
            .HasIndex(i => new { i.TenantId, i.PeriodStart, i.PeriodEnd })
            .IsUnique()
            .HasDatabaseName("IX_Invoice_Idempotency");

        // Tenant -> Users relationship
        modelBuilder.Entity<User>()
            .HasOne(u => u.Tenant)
            .WithMany()
            .HasForeignKey(u => u.TenantId);

        // Tenant -> ApiKeys relationship
        modelBuilder.Entity<ApiKey>()
            .HasOne(a => a.Tenant)
            .WithMany()
            .HasForeignKey(a => a.TenantId);

        // Tenant -> UsageRecords relationship
        modelBuilder.Entity<UsageRecord>()
            .HasOne(u => u.Tenant)
            .WithMany()
            .HasForeignKey(u => u.TenantId);

        // Tenant -> Invoices relationship
        modelBuilder.Entity<Invoice>()
            .HasOne(i => i.Tenant)
            .WithMany()
            .HasForeignKey(i => i.TenantId);
            
        // Tenant -> AuditLogs relationship
        modelBuilder.Entity<AuditLog>()
            .HasOne(a => a.Tenant)
            .WithMany()
            .HasForeignKey(a => a.TenantId);

        // Index on ApiKey for fast lookups
        modelBuilder.Entity<ApiKey>()
            .HasIndex(a => a.KeyHash)
            .IsUnique();

        // Index on UsageRecord Timestamp for performance
        modelBuilder.Entity<UsageRecord>()
            .HasIndex(u => u.Timestamp);

        // Composite indexes for multi-tenant query filter performance
        modelBuilder.Entity<ApiKey>()
            .HasIndex(a => new { a.TenantId, a.Environment })
            .HasDatabaseName("IX_ApiKey_Tenant_Environment");

        modelBuilder.Entity<PdfJob>()
            .HasIndex(j => new { j.TenantId, j.Environment, j.CreatedAt })
            .HasDatabaseName("IX_PdfJob_Tenant_Environment_Created");

        modelBuilder.Entity<UsageRecord>()
            .HasIndex(u => new { u.TenantId, u.Environment, u.Timestamp })
            .HasDatabaseName("IX_UsageRecord_Tenant_Environment_Timestamp");

        // Global Query Filters for Strict Multi-Tenant Isolation
        modelBuilder.Entity<User>().HasQueryFilter(u => _tenantProvider.TenantId == null || u.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<ApiKey>().HasQueryFilter(a => _tenantProvider.TenantId == null || (a.TenantId == _tenantProvider.TenantId && a.Environment == _environmentProvider.ActiveEnvironment));
        modelBuilder.Entity<UsageRecord>().HasQueryFilter(u => _tenantProvider.TenantId == null || (u.TenantId == _tenantProvider.TenantId && u.Environment == _environmentProvider.ActiveEnvironment));
        modelBuilder.Entity<Invoice>().HasQueryFilter(i => _tenantProvider.TenantId == null || i.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<AuditLog>().HasQueryFilter(a => _tenantProvider.TenantId == null || a.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<TwoFactorRecoveryCode>().HasQueryFilter(r => _tenantProvider.TenantId == null || r.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<WebhookEndpoint>().HasQueryFilter(w => _tenantProvider.TenantId == null || (w.TenantId == _tenantProvider.TenantId && w.Environment == _environmentProvider.ActiveEnvironment));
        modelBuilder.Entity<SavedTemplate>().HasQueryFilter(s => _tenantProvider.TenantId == null || (s.TenantId == _tenantProvider.TenantId && s.DeletedAt == null && s.Environment == _environmentProvider.ActiveEnvironment));
        modelBuilder.Entity<PdfJob>().HasQueryFilter(j => _tenantProvider.TenantId == null || (j.TenantId == _tenantProvider.TenantId && j.Environment == _environmentProvider.ActiveEnvironment));
        modelBuilder.Entity<StorageProvider>().HasQueryFilter(s => _tenantProvider.TenantId == null || (s.TenantId == _tenantProvider.TenantId && s.Environment == _environmentProvider.ActiveEnvironment));
        modelBuilder.Entity<SSOConfiguration>().HasQueryFilter(s => _tenantProvider.TenantId == null || s.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<SCIMProvisioning>().HasQueryFilter(s => _tenantProvider.TenantId == null || s.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<Notification>().HasQueryFilter(n => _tenantProvider.TenantId == null || n.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<SdkUsage>().HasQueryFilter(s => _tenantProvider.TenantId == null || s.TenantId == _tenantProvider.TenantId);

        // SaaS Production filters
        modelBuilder.Entity<TenantEntitlement>().HasQueryFilter(e => _tenantProvider.TenantId == null || e.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<Invitation>().HasQueryFilter(i => _tenantProvider.TenantId == null || i.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<UsageAggregate>().HasQueryFilter(u => _tenantProvider.TenantId == null || u.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<EmailLog>().HasQueryFilter(l => _tenantProvider.TenantId == null || l.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<Asset>().HasQueryFilter(a => _tenantProvider.TenantId == null || a.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<SupportTicket>().HasQueryFilter(s => _tenantProvider.TenantId == null || s.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<PersonalAccessToken>().HasQueryFilter(p => _tenantProvider.TenantId == null || p.TenantId == _tenantProvider.TenantId);
    }

}
