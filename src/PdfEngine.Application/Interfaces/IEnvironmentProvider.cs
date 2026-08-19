using System;

namespace PdfEngine.Application.Interfaces;

public interface IEnvironmentProvider
{
    string ActiveEnvironment { get; }
}
