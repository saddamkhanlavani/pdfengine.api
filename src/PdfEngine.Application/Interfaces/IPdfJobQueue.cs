using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Interfaces;

public interface IPdfJobQueue
{
    ValueTask EnqueueAsync(PdfJob job, CancellationToken cancellationToken = default);
    ValueTask<PdfJob> DequeueAsync(CancellationToken cancellationToken = default);
}
