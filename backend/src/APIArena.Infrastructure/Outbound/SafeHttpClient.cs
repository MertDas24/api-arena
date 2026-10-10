using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using APIArena.Application.Options;
using APIArena.Domain.Outbound;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure.Outbound;

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>Opens the TCP connection to an already validated address (tests substitute a local endpoint).</summary>
public interface IConnectionFactory
{
    Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct);
}

public sealed class SocketConnectionFactory : IConnectionFactory
{
    public async Task<Stream> ConnectAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Policy knobs. <see cref="AllowHttp"/> is only ever set by tests (never bound from configuration).</summary>
public sealed record OutboundPolicy(IReadOnlyCollection<int> AllowedPorts, IReadOnlyCollection<string> DevLocalOrigins, bool AllowHttp = false);

public sealed record OutboundResult(bool Success, int? Status, string? Body, string? Error, int DurationMs)
{
    public static OutboundResult Fail(string error, int durationMs, int? status = null) => new(false, status, null, error, durationMs);
}

internal sealed class OutboundBlockedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// The ONLY HttpClient used for user-declared destinations (verification, delivery, replay, adapter) — WH-03/WH-04:
/// - URL re-validated on every call (scheme, userinfo, fragment, port, IP literals, blocked names);
/// - DNS resolved inside the connect callback for every new connection; all A/AAAA answers must be public, and the
///   socket connects to one of exactly those validated addresses (no TOCTOU / DNS rebinding window);
/// - TLS is negotiated by SocketsHttpHandler for the original host name (SNI + certificate validation unchanged);
/// - redirects are never followed; no proxy, cookies or decompression; bounded connect/request time and response size;
/// - errors are reduced to codes so internal addresses or connection details never reach users.
/// </summary>
public sealed class SafeHttpClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly IDnsResolver _dns;
    private readonly IConnectionFactory _connections;
    private readonly OutboundPolicy _policy;
    private readonly WebhookOptions _options;
    private readonly ILogger<SafeHttpClient> _logger;

    public SafeHttpClient(IDnsResolver dns, IConnectionFactory connections, IOptions<WebhookOptions> options, IHostEnvironment env, ILogger<SafeHttpClient> logger)
        : this(dns, connections, options.Value, DefaultPolicy(options.Value, env), logger)
    {
    }

    public SafeHttpClient(IDnsResolver dns, IConnectionFactory connections, WebhookOptions options, OutboundPolicy policy, ILogger<SafeHttpClient> logger)
    {
        _dns = dns;
        _connections = connections;
        _options = options;
        _policy = policy;
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 16,
            SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 },
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("APIArena-Webhooks/1.0");
    }

    public static OutboundPolicy DefaultPolicy(WebhookOptions o, IHostEnvironment env) =>
        new(o.AllowedPorts, o.DevLocalReceiver.Enabled && !env.IsProduction() ? o.DevLocalReceiver.Origins : []);

    private static readonly HashSet<string> AllowedHeaderPrefixes = new(StringComparer.OrdinalIgnoreCase) { "X-Arena-" };

    public async Task<OutboundResult> PostJsonAsync(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        int Elapsed() => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var (target, error) = UrlPolicy.Validate(url, _policy.AllowedPorts, _policy.DevLocalOrigins);
        if (target is null && _policy.AllowHttp && Uri.TryCreate(url, UriKind.Absolute, out var testUri) && testUri.Scheme == Uri.UriSchemeHttp)
        {
            (target, error) = UrlPolicy.Validate("https" + url[4..], _policy.AllowedPorts);
            target = target is null ? null : target with { Uri = testUri };
        }

        if (target is null)
        {
            APIArena.Application.Ops.ArenaMetrics.OutboundBlocked("url_policy");
            return OutboundResult.Fail(error ?? "url_invalid", Elapsed());
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target.Uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            // Header allow-list: only platform X-Arena-* headers; CR/LF and hop-by-hop/credential headers are impossible.
            if (!AllowedHeaderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || value.Any(char.IsControl) || name.Any(c => char.IsControl(c) || c == ':'))
            {
                return OutboundResult.Fail("header_not_allowed", Elapsed());
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                return OutboundResult.Fail("redirect_not_followed", Elapsed(), status);
            }

            var (text, tooLarge) = await ReadLimitedAsync(response, _options.MaxResponseBytes, cts.Token);
            if (tooLarge)
            {
                return OutboundResult.Fail("response_too_large", Elapsed(), status);
            }

            return new OutboundResult(response.IsSuccessStatusCode, status, text, response.IsSuccessStatusCode ? null : $"http_{status}", Elapsed());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OutboundResult.Fail("timeout", Elapsed());
        }
        catch (HttpRequestException ex)
        {
            var code = Classify(ex);
            if (code == "blocked_address")
            {
                APIArena.Application.Ops.ArenaMetrics.OutboundBlocked(code);
            }
            _logger.LogInformation("Outbound request failed with {Code}", code); // no host/IP/exception text in logs either
            return OutboundResult.Fail(code, Elapsed());
        }
    }

    private static string Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case OutboundBlockedException blocked:
                    return blocked.Code;
                case AuthenticationException:
                    return "tls_failed";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return "dns_failed";
                case SocketException:
                    return "connect_failed";
            }
        }

        return "connection_failed";
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var devLocal = _policy.DevLocalOrigins.Any(o => Uri.TryCreate(o, UriKind.Absolute, out var u)
            && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase) && u.Port == port);

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _dns.ResolveAsync(host, ct);
        }
        catch (SocketException)
        {
            throw new OutboundBlockedException("dns_failed");
        }

        if (!devLocal && !IpPolicy.AllPubliclyRoutable(addresses))
        {
            throw new OutboundBlockedException("blocked_address");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            try
            {
                return await _connections.ConnectAsync(address, port, ct);
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw last ?? new OutboundBlockedException("dns_failed");
    }

    private static async Task<(string? Text, bool TooLarge)> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > max)
        {
            return (null, true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > max)
            {
                return (null, true);
            }

            ms.Write(buffer, 0, read);
        }

        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    public void Dispose() => _client.Dispose();
}
