using Microsoft.EntityFrameworkCore;
using PdfEngine.Domain.Entities;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Data;

public class PdfEngineDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;

    public PdfEngineDbContext(
        DbContextOptions<PdfEngineDbContext> options,
        ITenantProvider tenantProvider) : base(options)
    {
        _tenantProvider = tenantProvider;
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
    public DbSet<DownloadAccessLog> DownloadAccessLogs { get; set; }
    public DbSet<PdfJob> PdfJobs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Primary key configuration
        modelBuilder.Entity<PdfJob>().HasKey(j => j.JobId);

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

        // Global Query Filters for Strict Multi-Tenant Isolation
        modelBuilder.Entity<User>().HasQueryFilter(u => _tenantProvider.TenantId == null || u.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<ApiKey>().HasQueryFilter(a => _tenantProvider.TenantId == null || a.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<UsageRecord>().HasQueryFilter(u => _tenantProvider.TenantId == null || u.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<Invoice>().HasQueryFilter(i => _tenantProvider.TenantId == null || i.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<AuditLog>().HasQueryFilter(a => _tenantProvider.TenantId == null || a.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<TwoFactorRecoveryCode>().HasQueryFilter(r => _tenantProvider.TenantId == null || r.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<WebhookEndpoint>().HasQueryFilter(w => _tenantProvider.TenantId == null || w.Environment == null || w.TenantId == _tenantProvider.TenantId);
        modelBuilder.Entity<SavedTemplate>().HasQueryFilter(s => _tenantProvider.TenantId == null || (s.TenantId == _tenantProvider.TenantId && s.DeletedAt == null));
        modelBuilder.Entity<PdfJob>().HasQueryFilter(j => _tenantProvider.TenantId == null || j.TenantId == _tenantProvider.TenantId);
    }
}
