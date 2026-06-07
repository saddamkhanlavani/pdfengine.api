using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Infrastructure.Security;

public class InMemoryRateLimiter : IRateLimiter
{
    private static readonly ConcurrentDictionary<string, List<DateTime>> _requests = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsAllowedAsync(string apiKey, int limitPerMinute)
    {
        var now = DateTime.UtcNow;
        var timestamps = _requests.GetOrAdd(apiKey, _ => new List<DateTime>());

        lock (timestamps)
        {
            // Clean up timestamps older than 1 minute
            timestamps.RemoveAll(t => t < now.AddMinutes(-1));

            if (timestamps.Count >= limitPerMinute)
            {
                return Task.FromResult(false);
            }

            timestamps.Add(now);
            return Task.FromResult(true);
        }
    }
}
