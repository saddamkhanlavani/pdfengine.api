using System.Threading;
using System.Threading.Tasks;
using PdfEngine.Application.Common;
using PdfEngine.Application.Features.Pdf.Commands;

namespace PdfEngine.Application.Interfaces;

public interface IPdfService
{
    Task<Result<byte[]>> GenerateAsync(GeneratePdfCommand command, CancellationToken cancellationToken = default);
    Task<Result<byte[]>> MergeAsync(MergePdfCommand command, CancellationToken cancellationToken = default);
    Task<Result<byte[]>> TransformAsync(TransformPdfCommand command, CancellationToken cancellationToken = default);
}
