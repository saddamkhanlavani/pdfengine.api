using System.Text.Json;
using System.Threading.Tasks;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using StackExchange.Redis;

namespace PdfEngine.Infrastructure.Storage;

public class RedisPdfJobStorage : IPdfJobStorage
{
    private readonly IConnectionMultiplexer _redis;
    private const string JobHashKey = "pdf_jobs_state";

    public RedisPdfJobStorage(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task SaveJobAsync(PdfJob job)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(job);
        
        // Store inside a Redis Hash map where Field = JobId, Value = Job JSON
        await db.HashSetAsync(JobHashKey, job.JobId, json);
    }

    public async Task<PdfJob?> GetJobAsync(string jobId)
    {
        var db = _redis.GetDatabase();
        var result = await db.HashGetAsync(JobHashKey, jobId);

        if (!result.HasValue)
        {
            return null;
        }

        return JsonSerializer.Deserialize<PdfJob>(result.ToString());
    }

    public async Task UpdateJobAsync(PdfJob job)
    {
        // Updating is the same as saving in this model
        await SaveJobAsync(job);
    }
}
