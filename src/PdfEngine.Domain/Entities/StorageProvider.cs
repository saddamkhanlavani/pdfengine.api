using System;

namespace PdfEngine.Domain.Entities;

public class StorageProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    
    public string ProviderType { get; set; } = "S3"; // S3, AzureBlob, GCS
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string? Endpoint { get; set; } // Optional endpoint for custom S3/MinIO
    
    public string AccessKeyEncrypted { get; set; } = string.Empty;
    public string SecretKeyEncrypted { get; set; } = string.Empty;
    
    public bool IsActive { get; set; } = true;
    public string Environment { get; set; } = "Production"; // "Production" or "Development"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
