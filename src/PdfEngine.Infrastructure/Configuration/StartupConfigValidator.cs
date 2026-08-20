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

    /// <summary>
    /// Values that ship in the repository. Any of these reaching a non-Development
    /// environment means a real secret was never supplied, and the service must not start.
    ///
    /// Length and presence checks cannot catch this on their own: the committed JWT key is
    /// 49 bytes of well-formed nonsense and satisfies every structural rule there is. The
    /// only thing that distinguishes it from a real secret is that everyone with the
    /// repository has it.
    /// </summary>
    private static readonly string[] KnownDevelopmentSecrets =
    {
        // The original committed key. Kept in this list FOREVER even though it has been
        // purged from history and rotated: it was published on a public remote, so any
        // clone taken before the rewrite still has it, and a config that resurrects it
        // must never start outside Development.
        "***PURGED-ROTATED-SECRET***",
        "dev-only-Hgv5tVhqJXFgSKgxV0A4m7X9NCwBvvldOsif8n1x",
        "sk_test_your_key",
        "minioadmin",
        "pdfpassword",
        "test-api-key-123",
    };

    /// <param name="environmentName">
    /// ASPNETCORE_ENVIRONMENT. Development is allowed to run on the committed defaults —
    /// that is what they are for — and every other environment is not.
    /// </param>
    public static void Validate(IConfiguration config, string? environmentName = null)
    {
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
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

        // Fail CLOSED, and fail at boot. A service that starts with a publicly known
        // signing key issues tokens anyone can forge for any tenant, including admin, and
        // nothing about it looks wrong from the outside — which is precisely why this has
        // to stop the process rather than log a warning somebody reads later.
        if (!isDevelopment)
        {
            var exposed = new List<string>();
            foreach (var (key, value) in new[]
                     {
                         ("Jwt:Key", jwtKey),
                         ("Stripe:SecretKey", config["Stripe:SecretKey"]),
                         ("AWS:AccessKey", config["AWS:AccessKey"]),
                         ("AWS:SecretKey", config["AWS:SecretKey"]),
                     })
            {
                if (!string.IsNullOrWhiteSpace(value)
                    && Array.Exists(KnownDevelopmentSecrets,
                                    known => string.Equals(known, value, StringComparison.Ordinal)))
                {
                    exposed.Add(key);
                }
            }

            var connection = dbConn ?? string.Empty;
            if (Array.Exists(KnownDevelopmentSecrets, known => connection.Contains(known, StringComparison.Ordinal)))
            {
                exposed.Add("ConnectionStrings:DefaultConnection");
            }

            if (exposed.Count > 0)
            {
                throw new InvalidOperationException(
                    $"CRITICAL CONFIGURATION ERROR: {string.Join(", ", exposed)} still hold values committed to this repository, " +
                    $"and the environment is '{environmentName ?? "(unset)"}' rather than Development. Supply real secrets through " +
                    "the environment (for example Jwt__Key) or a secret store. The service will not start with a signing key " +
                    "that anyone with the source can use to forge tokens.");
            }
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
