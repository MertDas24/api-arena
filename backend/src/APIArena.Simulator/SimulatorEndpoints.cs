using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using APIArena.Application.Options;
using APIArena.Application.Simulation;
using APIArena.Domain.Entities;
using APIArena.Domain.Privacy;
using APIArena.Domain.Runs;
using APIArena.Domain.Scenarios;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace APIArena.Simulator;

public static partial class SimulatorEndpoints
{
    public const string PlatformHeader = "X-Arena-Platform";
    private const string ExposedHeaders = "Retry-After, X-Arena-Simulated, X-Arena-Sequence, X-Arena-Attempt, X-Arena-Platform, X-Correlation-ID";
    private const string AllowedRequestHeaders = "authorization, content-type, idempotency-key, x-request-id";

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex OperationKeyPattern();

    public static IServiceCollection AddSimulator(this IServiceCollection services)
    {
        // Bounded cost before authentication: per-instance concurrency + per-client-IP request budget.
        services.AddSingleton(sp =>
        {
            var perIp = sp.GetRequiredService<IOptions<LimitOptions>>().Value.SimulatorRequestsPerIpPerMinute;
            return PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions { PermitLimit = 500, QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                    RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = perIp,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    })));
        });
        return services;
    }

    public static IEndpointRouteBuilder MapSimulator(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Json(new { service = "apiarena-simulator", plane = "data" }));
        app.MapMethods("/sim/runs/{runId:guid}/{**route}", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "HEAD"], Handle);
        return app;
    }

    private static async Task Handle(HttpContext http, Guid runId, string? route, SimulationService sim, IOptions<LimitOptions> limits,
        TimeProvider clock, PartitionedRateLimiter<HttpContext> concurrency, ILoggerFactory loggers)
    {
        var started = Stopwatch.GetTimestamp();
        var receivedAt = clock.GetUtcNow();
        var ct = http.RequestAborted;
        var l = limits.Value;
        route = (route ?? "").Trim('/');

        if ((http.Request.Path.Value?.Length ?? 0) + http.Request.QueryString.Value?.Length > l.MaxUrlLength)
        {
            await Platform(http, 414, "url_too_long", "URL çok uzun.");
            return;
        }

        using var lease = await concurrency.AcquireAsync(http, 1, ct);
        if (!lease.IsAcquired)
        {
            if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            {
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                http.Response.Headers[PlatformHeader] = "client-rate-limited";
                await Platform(http, 429, "client_rate_limited", "Bu istemci adresinden çok fazla istek geldi (platform limiti). Bu bir senaryo yanıtı değildir.");
                return;
            }

            await Platform(http, 503, "simulator_overloaded", "Simülatör şu anda kapasite sınırında. Bu bir senaryo yanıtı değildir.");
            return;
        }

        if (HttpMethods.IsOptions(http.Request.Method))
        {
            await Preflight(http, runId, route, sim, ct);
            return;
        }

        // 1) Credential shape check — no database work for missing/malformed keys. Cookies are ignored entirely.
        var auth = http.Request.Headers.Authorization.ToString();
        var key = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..].Trim() : null;
        if (!RunKeys.LooksValid(key))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await Platform(http, 401, "missing_or_invalid_run_key", "Geçerli bir run-key gerekli: Authorization: Bearer <run-key>.");
            return;
        }

        // 2) Key → run binding (hash only; revoked/expired keys fail the same way as unknown ones).
        var run = await sim.AuthenticateAsync(runId, key!, ct);
        if (run is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
            await Platform(http, 401, "invalid_run_key", "Anahtar geçersiz, iptal edilmiş veya süresi dolmuş.");
            return;
        }

        ApplyCors(http, run);


        // 3) Fixture route (bounded catalog per run) and idempotency key are validated before reading the body.
        var opKey = http.Request.Headers["Idempotency-Key"].ToString();
        var baseRequest = new InboundRequest(http.Request.Method, route, receivedAt, string.IsNullOrEmpty(opKey) ? null : opKey,
            Masking.HeadersJson(http.Request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value.ToString()))), 0, null, false);

        if (run.Route is null || !string.Equals(run.Route.Route, route, StringComparison.Ordinal) || !string.Equals(run.Route.Method, http.Request.Method, StringComparison.OrdinalIgnoreCase))
        {
            var status = await sim.RecordRejectedAsync(run, baseRequest with { OperationKey = null }, 404, "unknown_fixture_route", CancellationToken.None);
            if (await WritePlanRejection(http, status))
            {
                return;
            }

            await Platform(http, 404, "unknown_fixture_route", run.Route is null
                ? "Bu koşunun HTTP fixture'ı yok."
                : $"Bu koşu yalnızca {run.Route.Method} /{run.Route.Route} kabul eder.");
            return;
        }

        if (!string.IsNullOrEmpty(opKey) && !OperationKeyPattern().IsMatch(opKey))
        {
            var status = await sim.RecordRejectedAsync(run, baseRequest with { OperationKey = null }, 400, "invalid_idempotency_key", CancellationToken.None);
            if (!await WritePlanRejection(http, status))
            {
                await Platform(http, 400, "invalid_idempotency_key", "Idempotency-Key 1–128 karakter olmalı: harf, rakam, . _ : -");
            }

            return;
        }

        // 4) Body: bounded read; oversize traffic is recorded as rejected, never as a silent success.
        byte[] body;
        try
        {
            body = await ReadBodyAsync(http, l.MaxRequestBodyBytes, ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            var status = await sim.RecordRejectedAsync(run, baseRequest, 413, "body_too_large", CancellationToken.None);
            if (!await WritePlanRejection(http, status))
            {
                await Platform(http, 413, "body_too_large", $"İstek gövdesi en fazla {l.MaxRequestBodyBytes / 1024} KiB olabilir.");
            }

            return;
        }

        var (preview, truncated) = run.CaptureBodies && body.Length > 0 ? Masking.BodyPreview(body, l.BodyPreviewChars) : (null, false);
        var request = baseRequest with { BodyBytes = body.Length, BodyPreview = preview, BodyTruncated = truncated };

        // 5) Atomic plan (short transaction), then wait/respond outside any transaction.
        var planStarted = Stopwatch.GetTimestamp();
        var plan = await sim.PlanAsync(run, request, ct);
        APIArena.Application.Ops.ArenaMetrics.PlanMs(Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds);
        if (await WritePlanRejection(http, plan.Status))
        {
            return;
        }

        var response = plan.Response!;
        APIArena.Application.Ops.ArenaMetrics.SimulatorRequest("scenario");
        var outcome = DeliveryOutcome.Sent;
        try
        {
            if (response.DelayMs > 0)
            {
                if (!await DelayWhileRunning(sim, run.RunId, Math.Min(response.DelayMs, l.MaxDelayMs), clock, ct))
                {
                    outcome = DeliveryOutcome.Rejected; // planned response was not sent: the run ended during the delay
                    http.Response.Headers[PlatformHeader] = "run-ended-during-delay";
                    await Platform(http, 409, "run_not_running", "Gecikme sırasında koşu sona erdi; senaryo yanıtı gönderilmedi.");
                    return;
                }
            }

            http.Response.StatusCode = response.Status;
            http.Response.Headers["X-Arena-Simulated"] = "true";
            http.Response.Headers["X-Arena-Sequence"] = plan.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.Headers["X-Arena-Attempt"] = plan.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var (name, value) in response.Headers)
            {
                if (string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase))
                {
                    http.Response.Headers.RetryAfter = value; // the only scenario-controlled header
                }
            }

            if (response.Status != 204 && !HttpMethods.IsHead(http.Request.Method) && response.Body.Length > 0)
            {
                http.Response.ContentType = response.ContentType;
                var bytes = Encoding.UTF8.GetBytes(response.Body);
                await http.Response.Body.WriteAsync(bytes.AsMemory(0, Math.Min(bytes.Length, l.MaxResponseBodyBytes)), ct);
            }

            await http.Response.CompleteAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = DeliveryOutcome.ClientDisconnected; // transport evidence: the client closed the connection
        }
        catch (IOException)
        {
            outcome = DeliveryOutcome.Unknown; // write failed for a reason we cannot attribute
        }
        finally
        {
            var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            try
            {
                await sim.CompleteAsync(run.RunId, plan.ObservationId, outcome, elapsed, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // If this write fails the observation stays Planned and is reported as Unknown at finalisation.
                loggers.CreateLogger("APIArena.Simulator").LogError("Could not record response outcome ({Error})", ex.GetType().Name);
            }
        }
    }

    /// <summary>Cancellable delay that also stops early (≤ 1 s) if the run is finished, cancelled or expired elsewhere.</summary>
    private static async Task<bool> DelayWhileRunning(SimulationService sim, Guid runId, int delayMs, TimeProvider clock, CancellationToken ct)
    {
        var remaining = TimeSpan.FromMilliseconds(delayMs);
        while (remaining > TimeSpan.Zero)
        {
            var step = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            await Task.Delay(step, clock, ct);
            remaining -= step;
            if (remaining > TimeSpan.Zero && !await sim.IsRunningAsync(runId, ct))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http, int max, CancellationToken ct)
    {
        if (http.Request.ContentLength > max)
        {
            throw new BadHttpRequestException("Request body too large.", 413);
        }

        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = max;
        }

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > max)
            {
                throw new BadHttpRequestException("Request body too large.", 413);
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static async Task<bool> WritePlanRejection(HttpContext http, PlanStatus status)
    {
        switch (status)
        {
            case PlanStatus.QuotaExceeded:
                http.Response.Headers[PlatformHeader] = "run-quota-exceeded";
                await Platform(http, 409, "run_quota_exceeded", "Bu koşunun istek kotası doldu (platform limiti). Bu bir senaryo yanıtı değildir.");
                return true;
            case PlanStatus.Expired:
                http.Response.Headers[PlatformHeader] = "run-expired";
                await Platform(http, 410, "run_expired", "Koşunun süresi doldu.");
                return true;
            case PlanStatus.NotRunning:
                http.Response.Headers[PlatformHeader] = "run-not-running";
                await Platform(http, 409, "run_not_running", "Koşu çalışmıyor (başlatılmadı veya bitti).");
                return true;
            default:
                return false;
        }
    }

    /// <summary>Platform (non-scenario) responses are ProblemDetails marked with X-Arena-Platform so they are never mistaken for simulated errors.</summary>
    private static Task Platform(HttpContext http, int status, string code, string title)
    {
        APIArena.Application.Ops.ArenaMetrics.SimulatorRequest(code is "unknown_fixture_route" or "invalid_idempotency_key" or "body_too_large" ? "rejected" : "platform");
        if (code is "run_quota_exceeded" or "client_rate_limited")
        {
            APIArena.Application.Ops.ArenaMetrics.Quota(code);
        }

        if (!http.Response.Headers.ContainsKey(PlatformHeader))
        {
            http.Response.Headers[PlatformHeader] = code.Replace('_', '-');
        }

        http.Response.StatusCode = status;
        return http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Status = status, Title = title, Type = $"https://apiarena.dev/problems/{code}", Extensions = { ["code"] = code } },
        }).AsTask();
    }

    private static void ApplyCors(HttpContext http, RunContext run)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && run.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            http.Response.Headers.AccessControlAllowOrigin = origin; // exact origin, never "*", never with credentials
            http.Response.Headers.AccessControlExposeHeaders = ExposedHeaders;
        }

        http.Response.Headers.Vary = "Origin";
    }

    private static async Task Preflight(HttpContext http, Guid runId, string route, SimulationService sim, CancellationToken ct)
    {
        var origin = http.Request.Headers.Origin.ToString();
        var run = origin.Length > 0 ? await sim.GetForPreflightAsync(runId, ct) : null;
        http.Response.Headers.Vary = "Origin";
        if (run?.Route is { } r && run.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) && r.Route == route
            && string.Equals(http.Request.Headers.AccessControlRequestMethod, r.Method, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.Headers.AccessControlAllowOrigin = origin;
            http.Response.Headers.AccessControlAllowMethods = r.Method;
            http.Response.Headers.AccessControlAllowHeaders = AllowedRequestHeaders;
            http.Response.Headers.AccessControlMaxAge = "600";
        }

        http.Response.StatusCode = 204;
    }
}
