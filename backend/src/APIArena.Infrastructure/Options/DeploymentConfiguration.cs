using Microsoft.Extensions.Configuration;

namespace APIArena.Infrastructure.Options;

public static class DeploymentConfiguration
{
    /// <summary>Optional read-only deployment secret file; environment variables remain authoritative.</summary>
    public static void AddDeploymentFile(this ConfigurationManager configuration)
    {
        var path = Environment.GetEnvironmentVariable("APIARENA_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(path))
            configuration.AddJsonFile(path, optional: false, reloadOnChange: false).AddEnvironmentVariables();
    }
}
