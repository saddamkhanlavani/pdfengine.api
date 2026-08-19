using System;
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

    /// <summary>
    /// Tenant the caller is authenticated as. REQUIRED for any caller-facing lookup:
    /// without it a job id alone was enough to read another tenant's job, which a
    /// cross-tenant test caught as a live data leak. Null means "no tenant scoping"
    /// and is only for trusted internal callers (e.g. the render worker), never for
    /// anything reachable from the API surface.
    /// </summary>
    public Guid? TenantId { get; set; }
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

        // Deliberately returns the SAME "not found" as a missing job rather than a
        // 403: telling a caller "this exists but isn't yours" confirms the id is real
        // and leaks the existence of another tenant's document.
        if (request.TenantId.HasValue && job.TenantId != request.TenantId.Value)
        {
            return Result<PdfJob>.Fail(Error.NotFound("Job not found"));
        }

        return Result<PdfJob>.Success(job);
    }
}
