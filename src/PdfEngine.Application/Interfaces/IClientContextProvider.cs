using System;

namespace PdfEngine.Application.Interfaces;

public interface IClientContextProvider
{
    string? GetClientIp();
    string? GetUserAgent();
    string? GetAuthMechanism();
}
