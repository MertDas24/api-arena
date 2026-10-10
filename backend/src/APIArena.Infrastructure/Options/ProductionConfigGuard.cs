using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.SqlClient;

namespace APIArena.Infrastructure.Options;

/// <summary>
/// Fail-fast checks for settings that are acceptable in local development but dangerous in production.
/// Runs at host startup; any error aborts startup with a list of offending keys (never their values).
/// </summary>
public static class ProductionConfigGuard
{
    public static IReadOnlyList<string> Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = new List<string>();
        var connection = configuration.GetConnectionString("ApiArena");
        if (string.IsNullOrWhiteSpace(connection))
        {
            errors.Add("ConnectionStrings:ApiArena is required.");
        }

        if (!environment.IsProduction())
        {
            return errors;
        }

        try
        {
            if (connection is not null && new SqlConnectionStringBuilder(connection).TrustServerCertificate)
                errors.Add("ConnectionStrings:ApiArena must not disable TLS certificate validation (TrustServerCertificate=true) in Production.");
        }
        catch (ArgumentException) { errors.Add("ConnectionStrings:ApiArena is not a valid SQL Server connection string."); }

        if (string.IsNullOrWhiteSpace(configuration["ApiArena:DataProtection:CertificatePath"]))
            errors.Add("ApiArena:DataProtection:CertificatePath is required in Production (encrypted persistent Identity keys).");

        foreach (var key in new[] { "ApiArena:Urls:FrontendBaseUrl", "ApiArena:Urls:SimulatorPublicBaseUrl" })
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                errors.Add($"{key} must be an absolute URL.");
                continue;
            }

            if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{key} must not point to localhost in Production.");
            }

            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"{key} must use https in Production.");
            }
        }

        var frontend = configuration["ApiArena:Urls:FrontendBaseUrl"];
        var simulator = configuration["ApiArena:Urls:SimulatorPublicBaseUrl"];
        if (Uri.TryCreate(frontend, UriKind.Absolute, out var f) && Uri.TryCreate(simulator, UriKind.Absolute, out var s)
            && string.Equals(f.Host, s.Host, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Simulator must be served from a different host than the control panel in Production (cookie isolation).");
        }

        if (!string.Equals(configuration["ApiArena:Email:Mode"], "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("ApiArena:Email:Mode must be Smtp in Production (the file mail sink is development-only).");
        }
        else if (string.IsNullOrWhiteSpace(configuration["ApiArena:Email:SmtpHost"]))
        {
            errors.Add("ApiArena:Email:SmtpHost is required in Production.");
        }

        if (string.Equals(configuration["ApiArena:Webhooks:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(configuration["ApiArena:Webhooks:EgressRestrictionAttested"], "true", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("ApiArena:Webhooks:Enabled requires ApiArena:Webhooks:EgressRestrictionAttested=true after the network egress policy is deployed and verified (see docs/runbooks/EGRESS.md).");
            }

            if (string.Equals(configuration["ApiArena:Webhooks:DevLocalReceiver:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("ApiArena:Webhooks:DevLocalReceiver must never be enabled in Production.");
            }

            if (string.IsNullOrWhiteSpace(configuration["ApiArena:Webhooks:EncryptionKeys:Current"]))
            {
                errors.Add("ApiArena:Webhooks:EncryptionKeys:Current is required when webhooks are enabled.");
            }
        }

        if (string.Equals(configuration["ApiArena:Demo:SeedOnStartup"], "true", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("ApiArena:Demo:SeedOnStartup must not be enabled in Production.");
        }

        return errors;
    }

    public static void ThrowIfInvalid(IConfiguration configuration, IHostEnvironment environment)
    {
        var errors = Validate(configuration, environment);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }
    }
}
