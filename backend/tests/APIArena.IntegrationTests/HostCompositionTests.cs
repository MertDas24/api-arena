using APIArena.Infrastructure;
using APIArena.IntegrationTests.Infrastructure;
using APIArena.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace APIArena.IntegrationTests;

/// <summary>Every host's container must be resolvable (catches missing registrations that only one host had).</summary>
public sealed class HostCompositionTests(SqlDatabaseFixture db)
{
    [Fact]
    public void Worker_host_services_validate_on_build()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(TestSettings.Base(db.ConnectionString, Path.GetTempPath()));
        builder.Services.AddApiArenaCore(builder.Configuration, builder.Environment);
        builder.Services.AddWorkerJobs(builder.Configuration);
        using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.NotNull(provider.GetService<IHostedService>());
    }

    [Fact]
    public async Task Api_and_simulator_hosts_validate_on_build()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        Assert.NotNull(api.Services);
        Assert.NotNull(sim.Services);
    }
}
