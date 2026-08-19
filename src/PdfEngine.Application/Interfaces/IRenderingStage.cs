using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Application.DTOs;

namespace PdfEngine.Application.Interfaces;

public interface IRenderingStage
{
    Task ExecuteAsync(RenderingContext context, CancellationToken cancellationToken = default);
}
