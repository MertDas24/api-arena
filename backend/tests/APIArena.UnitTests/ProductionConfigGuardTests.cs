using APIArena.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace APIArena.UnitTests;

public sealed class ProductionConfigGuardTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> SafeProduction() => new()
    {
        ["ConnectionStrings:ApiArena"] = "Server=db.internal;Database=ApiArena;User Id=app;Password=x;Encrypt=true",
        ["ApiArena:Urls:FrontendBaseUrl"] = "https://app.apiarena.example",
        ["ApiArena:Urls:SimulatorPublicBaseUrl"] = "https://sim.apiarena-data.example",
        ["ApiArena:Email:Mode"] = "Smtp",
        ["ApiArena:Email:SmtpHost"] = "smtp.example",
        ["ApiArena:DataProtection:CertificatePath"] = "/run/secrets/identity.pfx",
    };

    [Fact]
    public void Safe_production_configuration_passes()
    {
        Assert.Empty(ProductionConfigGuard.Validate(Config(SafeProduction()), new Env("Production")));
    }

    [Theory]
    [InlineData("ConnectionStrings:ApiArena", "Server=db;Database=x;TrustServerCertificate=True", "TrustServerCertificate")]
    [InlineData("ApiArena:Urls:SimulatorPublicBaseUrl", "http://localhost:5151", "localhost")]
    [InlineData("ApiArena:Urls:SimulatorPublicBaseUrl", "https://app.apiarena.example", "different host")]
    [InlineData("ApiArena:Email:Mode", "File", "Smtp")]
    [InlineData("ApiArena:Webhooks:Enabled", "true", "EgressRestrictionAttested")]
    [InlineData("ApiArena:Demo:SeedOnStartup", "true", "SeedOnStartup")]
    [InlineData("ConnectionStrings:ApiArena", "Server=db;Trust Server Certificate=yes", "TrustServerCertificate")]
    [InlineData("ApiArena:DataProtection:CertificatePath", "", "CertificatePath")]
    public void Dangerous_production_settings_are_rejected(string key, string value, string expectedFragment)
    {
        var values = SafeProduction();
        values[key] = value;
        var errors = ProductionConfigGuard.Validate(Config(values), new Env("Production"));
        Assert.Contains(errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Development_allows_local_settings()
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:ApiArena"] = "Server=localhost;TrustServerCertificate=true",
            ["ApiArena:Email:Mode"] = "File",
        };
        Assert.Empty(ProductionConfigGuard.Validate(Config(values), new Env("Development")));
    }
}
