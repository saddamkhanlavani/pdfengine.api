using System.Threading;
using System.Threading.Tasks;
using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class TransformPdfCommandHandler : IRequestHandler<TransformPdfCommand, Result<byte[]>>
{
    private readonly IPdfService _pdfService;

    public TransformPdfCommandHandler(IPdfService pdfService)
    {
        _pdfService = pdfService;
    }

    public Task<Result<byte[]>> Handle(TransformPdfCommand request, CancellationToken cancellationToken)
        => _pdfService.TransformAsync(request, cancellationToken);
}
