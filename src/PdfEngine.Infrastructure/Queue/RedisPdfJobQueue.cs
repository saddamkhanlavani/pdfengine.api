using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;
using StackExchange.Redis;

namespace PdfEngine.Infrastructure.Queue;

public class RedisPdfJobQueue : IPdfJobQueue
{
    private readonly IConnectionMultiplexer _redis;
    private const string QueueKey = "pdf_jobs_queue";

    public RedisPdfJobQueue(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async ValueTask EnqueueAsync(PdfJob job, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(job);
        
        // Push to the left side of the list
        await db.ListLeftPushAsync(QueueKey, json);
    }

    public async ValueTask<PdfJob> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Pop from the right side of the list
            var result = await db.ListRightPopAsync(QueueKey);
            
            if (result.HasValue)
            {
                return JsonSerializer.Deserialize<PdfJob>(result.ToString())!;
            }

            // Simple delay-based polling to prevent high CPU usage when queue is empty.
            // In a more complex setup, you could use Redis Streams (XREAD BLOCK) for instant push.
            await Task.Delay(100, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }
}
