using System.Threading;
using System.Threading.Tasks;
using MediatR;
using PdfEngine.Application.Common;
using PdfEngine.Application.Interfaces;
using PdfEngine.Domain.Entities;

namespace PdfEngine.Application.Features.Jobs.Queries;

public class GetPdfJobQuery : IRequest<Result<PdfJob>>
{
    public string JobId { get; set; } = string.Empty;
}

public class GetPdfJobQueryHandler : IRequestHandler<GetPdfJobQuery, Result<PdfJob>>
{
    private readonly IPdfJobStorage _storage;

    public GetPdfJobQueryHandler(IPdfJobStorage storage)
    {
        _storage = storage;
    }

    public async Task<Result<PdfJob>> Handle(GetPdfJobQuery request, CancellationToken cancellationToken)
    {
        var job = await _storage.GetJobAsync(request.JobId);
        
        if (job == null)
        {
            return Result<PdfJob>.Fail(Error.NotFound("Job not found"));
        }

        return Result<PdfJob>.Success(job);
    }
}
