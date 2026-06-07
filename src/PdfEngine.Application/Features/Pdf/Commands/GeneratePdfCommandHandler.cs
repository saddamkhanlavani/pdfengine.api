using System.Threading;
using System.Threading.Tasks;
using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class GeneratePdfCommandHandler : IRequestHandler<GeneratePdfCommand, Result<byte[]>>
{
    private readonly IPdfService _pdfService;

    public GeneratePdfCommandHandler(IPdfService pdfService)
    {
        _pdfService = pdfService;
    }

    public Task<Result<byte[]>> Handle(GeneratePdfCommand request, CancellationToken cancellationToken)
    {
        return _pdfService.GenerateAsync(request, cancellationToken);
    }
}
