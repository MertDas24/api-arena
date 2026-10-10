using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using APIArena.Infrastructure.Outbound;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace APIArena.IntegrationTests.Infrastructure;

/// <summary>Controlled DNS: each host returns a scripted sequence of answers (last one repeats) — used for rebinding tests.</summary>
public sealed class FakeDns : IDnsResolver
{
    private readonly ConcurrentDictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentBag<string> Lookups { get; } = [];

    public FakeDns Set(string host, params string[][] answers)
    {
        _answers[host] = new Queue<IPAddress[]>(answers.Select(a => a.Select(IPAddress.Parse).ToArray()));
        return this;
    }

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Lookups.Add(host);
        if (!_answers.TryGetValue(host, out var q) || q.Count == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        lock (q)
        {
            return Task.FromResult(q.Count > 1 ? q.Dequeue() : q.Peek());
        }
    }
}

/// <summary>
/// Pretends that certain "public" IPs are reachable by routing them to a local test server. Any other address is
/// refused, so a test fails loudly if the client ever tries to connect somewhere it was not allowed to.
/// </summary>
public sealed class FakeConnections(int localPort, params string[] publicIps) : IConnectionFactory
{
    public ConcurrentBag<IPAddress> Attempts { get; } = [];

    public async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        Attempts.Add(address);
        if (!publicIps.Contains(address.ToString()))
        {
            throw new SocketException((int)SocketError.ConnectionRefused);
        }

        return await new SocketConnectionFactory().ConnectAsync(IPAddress.Loopback, localPort, ct);
    }
}

/// <summary>Minimal real HTTP server on 127.0.0.1 with an ephemeral port.</summary>
public sealed class LocalServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }

    private LocalServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public static async Task<LocalServer> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LocalServer(app, new Uri(address).Port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
