using APIArena.Application.Options;
using APIArena.Infrastructure.Outbound;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace APIArena.IntegrationTests;

/// <summary>
/// SSRF defences of the single outbound client (WH-03/WH-04) with controlled DNS and a local server standing in for a
/// "public" host. No real external site is contacted.
/// </summary>
public sealed class Stage13_SafeHttpClientTests
{
    private const string PublicIp = "93.184.216.34";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<LocalServer> ServerAsync() => await LocalServer.StartAsync(app =>
    {
        app.MapPost("/ok", () => Results.Json(new { ok = true }));
        app.MapPost("/redirect", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        app.MapPost("/big", () => Results.Text(new string('x', 100 * 1024)));
        app.MapPost("/slow", async (HttpContext ctx) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ctx.RequestAborted);
            return Results.Ok();
        });
    });

    private static SafeHttpClient Client(FakeDns dns, FakeConnections conns, int port, int timeoutSeconds = 5) =>
        new(dns, conns, new WebhookOptions { RequestTimeoutSeconds = timeoutSeconds, MaxResponseBytes = 64 * 1024 },
            new OutboundPolicy([port], [], AllowHttp: true), NullLogger<SafeHttpClient>.Instance);

    private static string Url(string host, int port, string path) => $"http://{host}:{port}{path}";

    [Fact]
    public async Task Public_host_connects_to_exactly_the_validated_address()
    {
        await using var server = await ServerAsync();
        var dns = new FakeDns().Set("hooks.customer-app.net", [PublicIp]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using var client = Client(dns, conns, server.Port);
        var result = await client.PostJsonAsync(Url("hooks.customer-app.net", server.Port, "/ok"), "{}", new Dictionary<string, string>(), Ct);
        Assert.True(result.Success, result.Error);
        Assert.All(conns.Attempts, a => Assert.Equal(PublicIp, a.ToString()));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fd00:ec2::254")]
    public async Task Hosts_resolving_to_internal_addresses_are_blocked_before_connecting(string ip)
    {
        await using var server = await ServerAsync();
        var dns = new FakeDns().Set("evil-rebind.attacker.net", [ip]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using var client = Client(dns, conns, server.Port);
        var result = await client.PostJsonAsync(Url("evil-rebind.attacker.net", server.Port, "/ok"), "{}", new Dictionary<string, string>(), Ct);
        Assert.False(result.Success);
        Assert.Equal("blocked_address", result.Error);
        Assert.Empty(conns.Attempts);
        Assert.DoesNotContain(ip, result.Error);
    }

    [Fact]
    public async Task Mixed_public_and_private_answers_are_blocked()
    {
        await using var server = await ServerAsync();
        var dns = new FakeDns().Set("mixed.attacker.net", [PublicIp, "10.0.0.5"]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using var client = Client(dns, conns, server.Port);
        var result = await client.PostJsonAsync(Url("mixed.attacker.net", server.Port, "/ok"), "{}", new Dictionary<string, string>(), Ct);
        Assert.Equal("blocked_address", result.Error);
        Assert.Empty(conns.Attempts);
    }

    [Fact]
    public async Task Dns_rebinding_is_caught_on_the_next_connection()
    {
        await using var server = await ServerAsync();
        // First lookup (e.g. at verification) is public, the next one flips to loopback.
        var dns = new FakeDns().Set("rebind.attacker.net", [PublicIp], ["127.0.0.1"]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using (var first = Client(dns, conns, server.Port))
        {
            Assert.True((await first.PostJsonAsync(Url("rebind.attacker.net", server.Port, "/ok"), "{}", new Dictionary<string, string>(), Ct)).Success);
        }

        using var second = Client(dns, conns, server.Port);
        var result = await second.PostJsonAsync(Url("rebind.attacker.net", server.Port, "/ok"), "{}", new Dictionary<string, string>(), Ct);
        Assert.Equal("blocked_address", result.Error);
        Assert.All(conns.Attempts, a => Assert.Equal(PublicIp, a.ToString()));
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        await using var server = await ServerAsync();
        var dns = new FakeDns().Set("hooks.customer-app.net", [PublicIp]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using var client = Client(dns, conns, server.Port);
        var result = await client.PostJsonAsync(Url("hooks.customer-app.net", server.Port, "/redirect"), "{}", new Dictionary<string, string>(), Ct);
        Assert.Equal("redirect_not_followed", result.Error);
        Assert.DoesNotContain(conns.Attempts, a => a.ToString() == "169.254.169.254");
    }

    [Fact]
    public async Task Oversized_responses_and_slow_targets_are_bounded()
    {
        await using var server = await ServerAsync();
        var dns = new FakeDns().Set("hooks.customer-app.net", [PublicIp]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using var client = Client(dns, conns, server.Port, timeoutSeconds: 1);
        Assert.Equal("response_too_large", (await client.PostJsonAsync(Url("hooks.customer-app.net", server.Port, "/big"), "{}", new Dictionary<string, string>(), Ct)).Error);
        Assert.Equal("timeout", (await client.PostJsonAsync(Url("hooks.customer-app.net", server.Port, "/slow"), "{}", new Dictionary<string, string>(), Ct)).Error);
    }

    [Theory]
    [InlineData("Cookie", "a=b")]
    [InlineData("Authorization", "Bearer x")]
    [InlineData("Host", "internal")]
    [InlineData("X-Arena-Test", "ok\r\nHost: evil")]
    public async Task Header_injection_and_forbidden_headers_are_rejected(string name, string value)
    {
        await using var server = await ServerAsync();
        var dns = new FakeDns().Set("hooks.customer-app.net", [PublicIp]);
        var conns = new FakeConnections(server.Port, PublicIp);
        using var client = Client(dns, conns, server.Port);
        var result = await client.PostJsonAsync(Url("hooks.customer-app.net", server.Port, "/ok"), "{}", new Dictionary<string, string> { [name] = value }, Ct);
        Assert.Equal("header_not_allowed", result.Error);
        Assert.Empty(conns.Attempts);
    }

    [Theory]
    [InlineData("https://user:pw@hooks.customer-app.net/ok", "url_userinfo_not_allowed")]
    [InlineData("https://hooks.customer-app.net/ok#x", "url_fragment_not_allowed")]
    [InlineData("https://2130706433/ok", "url_ip_literal_not_allowed")]
    public async Task Url_is_revalidated_on_every_call(string url, string error)
    {
        var dns = new FakeDns();
        var conns = new FakeConnections(1, PublicIp);
        using var client = new SafeHttpClient(dns, conns, new WebhookOptions(), new OutboundPolicy([443], []), NullLogger<SafeHttpClient>.Instance);
        var result = await client.PostJsonAsync(url, "{}", new Dictionary<string, string>(), Ct);
        Assert.Equal(error, result.Error);
        Assert.Empty(dns.Lookups);
    }
}
