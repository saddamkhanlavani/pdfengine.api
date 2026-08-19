using System.Collections.Generic;
using MediatR;
using PdfEngine.Application.Common;

namespace PdfEngine.Application.Features.Pdf.Commands;

public class MergePdfCommand : IRequest<Result<byte[]>>
{
    public string DocumentName { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
}
