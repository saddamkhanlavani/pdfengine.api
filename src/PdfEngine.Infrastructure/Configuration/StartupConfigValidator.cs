using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Text;

namespace PdfEngine.Infrastructure.Configuration;

public static class StartupConfigValidator
{
    /// <summary>
    /// External binaries the engine shells out to, and what stops working without them.
    /// Reported at boot rather than discovered on the request that needed them: an operator
    /// who reads one startup line can install the package, whereas a caller who gets a 400
    /// mid-render cannot.
    /// </summary>
    private static readonly (string Binary, string Feature)[] OptionalTools =
    {
        ("qpdf", "linearization (fast web view) — requests with linearize=true will FAIL until it is installed")
    };

    /// <summary>
    /// Reports which optional external tools are present. Deliberately not fatal: the
    /// engine renders perfectly well without them, and only the features that need them
    /// are affected — each of which fails loudly on its own.
    /// </summary>
    public static IReadOnlyList<string> CheckOptionalTools()
    {
        var findings = new List<string>();
        foreach (var (binary, feature) in OptionalTools)
        {
            var found = false;
            try
            {
                using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(binary)
                {
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (probe != null)
                {
                    probe.WaitForExit(5000);
                    found = true;
                }
            }
            catch (Exception)
            {
                found = false;
            }

            findings.Add(found
                ? $"OK: '{binary}' found."
                : $"MISSING: '{binary}' is not on PATH — {feature}.");
        }
        return findings;
    }

    public static void Validate(IConfiguration config)
    {
        var dbConn = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(dbConn))
        {
            throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: 'ConnectionStrings:DefaultConnection' is missing or empty.");
        }

        var redisConn = config["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConn))
        {
            throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: 'Redis:ConnectionString' is missing or empty.");
        }

        var jwtKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: 'Jwt:Key' is missing or empty.");
        }
        if (Encoding.UTF8.GetBytes(jwtKey).Length < 32)
        {
            throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: 'Jwt:Key' must be at least 256 bits (32 bytes) long.");
        }

        var jwtIssuer = config["Jwt:Issuer"];
        if (string.IsNullOrWhiteSpace(jwtIssuer))
        {
            throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: 'Jwt:Issuer' is missing or empty.");
        }

        var jwtAudience = config["Jwt:Audience"];
        if (string.IsNullOrWhiteSpace(jwtAudience))
        {
            throw new InvalidOperationException("CRITICAL CONFIGURATION ERROR: 'Jwt:Audience' is missing or empty.");
        }
    }
}
