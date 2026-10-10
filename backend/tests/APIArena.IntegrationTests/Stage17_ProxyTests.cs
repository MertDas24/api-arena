using APIArena.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace APIArena.IntegrationTests;

public sealed class Stage17_ProxyTests(SqlDatabaseFixture db)
{
    [Fact]
    public async Task Empty_proxy_configuration_trusts_loopback_only()
    {
        await using var api = new ApiFactory(db);
        var options = api.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.All(options.KnownProxies, ip => Assert.True(System.Net.IPAddress.IsLoopback(ip)));
        Assert.Empty(options.KnownIPNetworks);
    }

    [Fact]
    public async Task Explicit_proxy_configuration_does_not_trust_other_addresses()
    {
        await using var api = new ApiFactory(db);
        api.Overrides["ApiArena:Urls:TrustedProxies:0"] = "172.30.80.2";
        var options = api.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(System.Net.IPAddress.Parse("172.30.80.2"), Assert.Single(options.KnownProxies));
        Assert.Empty(options.KnownIPNetworks);
    }
}
