using System;
using System.Threading.Tasks;

namespace PdfEngine.Application.Interfaces;

public interface IWebhookService
{
    Task DispatchAsync(Guid tenantId, string eventType, object payload);
}
