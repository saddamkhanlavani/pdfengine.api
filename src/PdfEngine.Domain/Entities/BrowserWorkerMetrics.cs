using System;

namespace PdfEngine.Domain.Entities;

public class BrowserWorkerMetrics
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkerName { get; set; } = string.Empty;
    public double CpuUsagePercent { get; set; }
    public double MemoryUsageMb { get; set; }
    public int ActivePages { get; set; }
    public int TotalRendersProcessed { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
