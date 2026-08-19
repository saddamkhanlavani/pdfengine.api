using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using StackExchange.Redis;

namespace PdfEngine.Infrastructure.Storage;

public class RedisPdfJobStorage : IPdfJobStorage
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceScopeFactory _scopeFactory;
    private const string JobHashKey = "pdf_jobs_state";

    public RedisPdfJobStorage(IConnectionMultiplexer redis, IServiceScopeFactory scopeFactory)
    {
        _redis = redis;
        _scopeFactory = scopeFactory;
    }

    public async Task SaveJobAsync(PdfJob job)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(job);
        
        // Store inside a Redis Hash map where Field = JobId, Value = Job JSON
        await db.HashSetAsync(JobHashKey, job.JobId, json);

        // Sync to PostgreSQL database for persistence and admin reporting
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        var existing = await dbContext.PdfJobs.FindAsync(job.JobId);
        if (existing == null)
        {
            dbContext.PdfJobs.Add(job);
        }
        else
        {
            dbContext.Entry(existing).CurrentValues.SetValues(job);
        }
        await dbContext.SaveChangesAsync();
    }

    public async Task<PdfJob?> GetJobAsync(string jobId)
    {
        var db = _redis.GetDatabase();
        var result = await db.HashGetAsync(JobHashKey, jobId);

        if (result.HasValue)
        {
            return JsonSerializer.Deserialize<PdfJob>(result.ToString());
        }

        // Fallback to PostgreSQL database on Redis cache miss
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        return await dbContext.PdfJobs.FindAsync(jobId);
    }

    public async Task UpdateJobAsync(PdfJob job)
    {
        // Updating is the same as saving in this model
        await SaveJobAsync(job);
    }

    public async Task SaveSnapshotAsync(PdfJobSnapshot snapshot)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        dbContext.PdfJobSnapshots.Add(snapshot);
        await dbContext.SaveChangesAsync();
    }

    public async Task<PdfJobSnapshot?> GetSnapshotAsync(string jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PdfEngine.Infrastructure.Data.PdfEngineDbContext>();
        return await dbContext.PdfJobSnapshots.FindAsync(jobId);
    }
}
