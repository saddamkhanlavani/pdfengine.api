using System;

namespace PdfEngine.Application.Interfaces;

public interface ITenantProvider
{
    Guid? TenantId { get; }
}
