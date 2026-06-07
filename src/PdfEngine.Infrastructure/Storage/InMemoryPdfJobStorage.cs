using System.Collections.Concurrent;
using System.Threading.Tasks;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Infrastructure.Storage;

public class InMemoryPdfJobStorage : IPdfJobStorage
{
    private readonly ConcurrentDictionary<string, PdfJob> _jobs = new();

    public Task SaveJobAsync(PdfJob job)
    {
        _jobs[job.JobId] = job;
        return Task.CompletedTask;
    }

    public Task<PdfJob?> GetJobAsync(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    public Task UpdateJobAsync(PdfJob job)
    {
        // For ConcurrentDictionary, it's just replacing the value
        _jobs[job.JobId] = job;
        return Task.CompletedTask;
    }
}
