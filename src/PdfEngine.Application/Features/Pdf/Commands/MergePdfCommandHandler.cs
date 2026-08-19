using System.Threading;
using System.Threading.Tasks;
using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class MergePdfCommandHandler : IRequestHandler<MergePdfCommand, Result<byte[]>>
{
    private readonly IPdfService _pdfService;

    public MergePdfCommandHandler(IPdfService pdfService)
    {
        _pdfService = pdfService;
    }

    public Task<Result<byte[]>> Handle(MergePdfCommand request, CancellationToken cancellationToken)
        => _pdfService.MergeAsync(request, cancellationToken);
}
