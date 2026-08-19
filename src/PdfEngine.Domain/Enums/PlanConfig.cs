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
            IncludedQuota = 250,
            OveragePricePerPdf = 0,
            RequestsPerMinute = 5,
            MaxConcurrency = 1,
            MaxRenderDurationSeconds = 15,
            MaxPages = 5,
            MaxAssetDownloadMb = 5.0
        },
        [PlanType.Startup] = new()
        {
            IncludedQuota = 3000,
            OveragePricePerPdf = 0.003m,
            RequestsPerMinute = 30,
            MaxConcurrency = 2,
            MaxRenderDurationSeconds = 30,
            MaxPages = 30,
            MaxAssetDownloadMb = 15.0
        },
        [PlanType.Boost] = new()
        {
            IncludedQuota = 12000,
            OveragePricePerPdf = 0.002m,
            RequestsPerMinute = 100,
            MaxConcurrency = 5,
            MaxRenderDurationSeconds = 60,
            MaxPages = 100,
            MaxAssetDownloadMb = 30.0
        },
        [PlanType.Growth] = new()
        {
            IncludedQuota = 30000,
            OveragePricePerPdf = 0.001m,
            RequestsPerMinute = 250,
            MaxConcurrency = 10,
            MaxRenderDurationSeconds = 120,
            MaxPages = 300,
            MaxAssetDownloadMb = 50.0
        },
        [PlanType.Enterprise] = new()
        {
            IncludedQuota = 100000,
            OveragePricePerPdf = 0.0008m,
            RequestsPerMinute = 1000,
            MaxConcurrency = 100,
            MaxRenderDurationSeconds = 180,
            MaxPages = 1000,
            MaxAssetDownloadMb = 100.0
        }
    };
}
