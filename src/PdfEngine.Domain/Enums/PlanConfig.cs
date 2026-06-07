using System.Collections.Generic;

namespace PdfEngine.Domain.Enums;

public class PlanConfig
{
    public int IncludedQuota { get; set; }
    public decimal OveragePricePerPdf { get; set; }
    public int RequestsPerMinute { get; set; }
    public int MaxConcurrency { get; set; }
    
    // SLA Limits
    public int MaxRenderDurationSeconds { get; set; }
    public int MaxPages { get; set; }
    public double MaxAssetDownloadMb { get; set; }
}

public static class PlanRegistry
{
    public static readonly Dictionary<PlanType, PlanConfig> Plans = new()
    {
        [PlanType.Free] = new()
        {
            IncludedQuota = 100,
            OveragePricePerPdf = 0,
            RequestsPerMinute = 5,
            MaxConcurrency = 1,
            MaxRenderDurationSeconds = 15,
            MaxPages = 5,
            MaxAssetDownloadMb = 5.0
        },
        [PlanType.Pro] = new()
        {
            IncludedQuota = 5000,
            OveragePricePerPdf = 0.02m, // ₹1.5 approx
            RequestsPerMinute = 50,
            MaxConcurrency = 3,
            MaxRenderDurationSeconds = 60,
            MaxPages = 50,
            MaxAssetDownloadMb = 20.0
        },
        [PlanType.Enterprise] = new()
        {
            IncludedQuota = 50000,
            OveragePricePerPdf = 0.01m,
            RequestsPerMinute = 200,
            MaxConcurrency = 10,
            MaxRenderDurationSeconds = 180,
            MaxPages = 500,
            MaxAssetDownloadMb = 100.0
        }
    };
}
