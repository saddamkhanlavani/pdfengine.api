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
        var backoffMs = 1000;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var db = _redis.GetDatabase();
                var json = JsonSerializer.Serialize(job);
                await db.ListLeftPushAsync(QueueKey, json);
                return;
            }
            catch (RedisException)
            {
                backoffMs = Math.Min(10000, backoffMs * 2);
                await Task.Delay(backoffMs, cancellationToken);
            }
        }
    }

    public async ValueTask EnqueueDeadLetterAsync(PdfJob job)
    {
        try
        {
            var db = _redis.GetDatabase();
            var json = JsonSerializer.Serialize(job);
            await db.ListLeftPushAsync("pdf_jobs_queue:dlq", json);
        }
        catch
        {
            // Suppress exception on DLQ save to keep client unblocked
        }
    }

    public async ValueTask<PdfJob> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var backoffMs = 1000;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var db = _redis.GetDatabase();
                var result = await db.ListRightPopAsync(QueueKey);
                
                if (result.HasValue)
                {
                    var json = result.ToString();
                    try
                    {
                        var job = JsonSerializer.Deserialize<PdfJob>(json);
                        if (job != null)
                        {
                            return job;
                        }
                    }
                    catch
                    {
                        // Deserialization fails -> Poison message! Send directly to DLQ
                        try
                        {
                            await db.ListLeftPushAsync("pdf_jobs_queue:dlq", json);
                        }
                        catch {}
                        continue;
                    }
                }

                backoffMs = 1000; // reset backoff on successful loop
                await Task.Delay(100, cancellationToken);
            }
            catch (RedisException)
            {
                backoffMs = Math.Min(10000, backoffMs * 2);
                await Task.Delay(backoffMs, cancellationToken);
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }
}
