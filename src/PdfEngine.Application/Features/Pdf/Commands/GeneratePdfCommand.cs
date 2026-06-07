using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.DTOs;
using PdfEngine.Domain.Entities;
using PdfEngine.Domain.Enums;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class GeneratePdfCommand : IRequest<Result<byte[]>>
{
    public Tenant Client { get; set; } = null!;
    public ApiKey? ApiKey { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public DocumentType DocumentType { get; set; }
    public string HtmlContent { get; set; } = string.Empty;
    public RenderingOptions Options { get; set; } = new RenderingOptions();
}
