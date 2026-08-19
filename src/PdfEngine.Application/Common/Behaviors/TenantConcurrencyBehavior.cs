using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using PdfEngine.Application.Common;
using PdfEngine.Application.Features.Pdf.Commands;
using PdfEngine.Application.Interfaces;

namespace PdfEngine.Application.Common.Behaviors;

public class TenantConcurrencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : GeneratePdfCommand // We constrain it specifically here to access the Client
    where TResponse : Result<byte[]>
{
    private readonly ITenantEntitlementService _entitlementService;
    private readonly ILogger<TenantConcurrencyBehavior<TRequest, TResponse>> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _clientLocks = new();

    public TenantConcurrencyBehavior(ITenantEntitlementService entitlementService, ILogger<TenantConcurrencyBehavior<TRequest, TResponse>> logger)
    {
        _entitlementService = entitlementService;
        _logger = logger;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var client = request.Client;
        if (client == null)
        {
            _logger.LogWarning("TenantConcurrencyBehavior bypassed: No client associated with command.");
            return await next();
        }

        var plan = PdfEngine.Domain.Enums.PlanRegistry.Plans[client.Plan];
        var entitlement = await _entitlementService.GetEntitlementAsync(client.Id);
        int maxConcurrency = entitlement != null && entitlement.ConcurrentRenderLimit > 0
            ? entitlement.ConcurrentRenderLimit
            : plan.MaxConcurrency;

        var lockKey = request.ApiKey?.Key ?? client.Id.ToString();
        var clientSemaphore = _clientLocks.GetOrAdd(
            lockKey,
            _ => new SemaphoreSlim(maxConcurrency, maxConcurrency)
        );

        _logger.LogInformation("Client {ClientName} waiting for tenant-level render slot...", client.Name);
        await clientSemaphore.WaitAsync(cancellationToken);
        
        bool acquired = true;
        try
        {
            _logger.LogInformation("Client {ClientName} ACQUIRED tenant-level render slot.", client.Name);
            return await next();
        }
        finally
        {
            if (acquired)
            {
                clientSemaphore.Release();
                _logger.LogInformation("Client {ClientName} RELEASED tenant-level render slot.", client.Name);
            }
        }
    }
}
