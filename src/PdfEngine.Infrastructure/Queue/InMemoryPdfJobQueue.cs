using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Infrastructure.Queue;

public class InMemoryPdfJobQueue : IPdfJobQueue
{
    private readonly Channel<PdfJob> _queue;

    public InMemoryPdfJobQueue()
    {
        var options = new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        };
        _queue = Channel.CreateUnbounded<PdfJob>(options);
    }

    public async ValueTask EnqueueAsync(PdfJob job, CancellationToken cancellationToken = default)
    {
        await _queue.Writer.WriteAsync(job, cancellationToken);
    }

    public async ValueTask<PdfJob> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return await _queue.Reader.ReadAsync(cancellationToken);
    }

    public ValueTask EnqueueDeadLetterAsync(PdfJob job)
    {
        // No-op for in-memory queue
        return ValueTask.CompletedTask;
    }
}
