using System.Globalization;
using System.Text.Json;

namespace APIArena.Domain.Scenarios;

/// <summary>Inputs fixed atomically by the database before a decision is made (sequence, per-operation attempt, 429 window).</summary>
public sealed record EngineInput(long Sequence, int Attempt, DateTimeOffset ReceivedAt, DateTimeOffset? PreviousRateLimitedUntil);

/// <summary>A planned simulated response. Headers are limited to an allow-list; security headers are never overridable.</summary>
public sealed record SimResponse(int Status, int DelayMs, string Body, IReadOnlyDictionary<string, string> Headers, string ContentType = "application/json; charset=utf-8");

public sealed record FixtureRoute(string Method, string Route);

/// <summary>
/// Pure, deterministic scenario decisions (no clock, random, I/O). Same inputs → same response, which makes the
/// simulator testable and lets concurrent requests be ordered solely by the DB-assigned attempt/sequence.
/// </summary>
public static class ScenarioEngine
{
    public static FixtureRoute? RouteOf(string engine, string definitionJson)
    {
        if (Engines.IsWebhook(engine))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(definitionJson);
        var root = doc.RootElement;
        return root.TryGetProperty("method", out var m) && root.TryGetProperty("route", out var r)
            ? new FixtureRoute(m.GetString()!.ToUpperInvariant(), r.GetString()!)
            : null;
    }

    /// <summary>For rate-limit runs: the window end to store if this request is the first one of the run.</summary>
    public static DateTimeOffset? RateLimitWindowIfFirst(string engine, string definitionJson, DateTimeOffset receivedAt) =>
        engine == Engines.RateLimit
            ? receivedAt.AddSeconds(ScenarioJson.Deserialize<RateLimitDefinition>(definitionJson).RetryAfterSeconds)
            : null;

    public static SimResponse Decide(string engine, string definitionJson, EngineInput input) => engine switch
    {
        Engines.Retry503 => Retry503(ScenarioJson.Deserialize<Retry503Definition>(definitionJson), input),
        Engines.RateLimit => RateLimit(ScenarioJson.Deserialize<RateLimitDefinition>(definitionJson), input),
        Engines.Timeout => Timeout(ScenarioJson.Deserialize<TimeoutDefinition>(definitionJson), input),
        Engines.Custom => CustomScenarioCompiler.Decide(CustomScenarioCompiler.Parse(definitionJson), input),
        _ => throw new InvalidOperationException($"Engine '{engine}' has no HTTP fixture."),
    };

    private static SimResponse Retry503(Retry503Definition d, EngineInput input)
    {
        if (input.Attempt <= d.FailAttempts)
        {
            return Json(d.FailStatus, new { error = "service_unavailable", message = "Simulated temporary outage. Retry the same operation.", attempt = input.Attempt, simulated = true });
        }

        return Json(200, new { chargeId = $"ch_sim_{input.Sequence:D6}", status = "succeeded", amountCharged = 0, attempt = input.Attempt, simulated = true, note = "No real payment is processed." });
    }

    private static SimResponse RateLimit(RateLimitDefinition d, EngineInput input)
    {
        if (input.PreviousRateLimitedUntil is null)
        {
            // This request opened the window atomically.
            return Json(429, new { error = "rate_limited", retryAfterSeconds = d.RetryAfterSeconds, simulated = true },
                ("Retry-After", d.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture)));
        }

        if (input.ReceivedAt < input.PreviousRateLimitedUntil.Value)
        {
            var remaining = Math.Max(1, (int)Math.Ceiling((input.PreviousRateLimitedUntil.Value - input.ReceivedAt).TotalSeconds));
            return Json(429, new { error = "rate_limited", retryAfterSeconds = remaining, simulated = true, early = true },
                ("Retry-After", remaining.ToString(CultureInfo.InvariantCulture)));
        }

        return Json(200, new
        {
            orders = new[] { new { id = "ord_sim_1", total = 4200, currency = "TRY" }, new { id = "ord_sim_2", total = 1250, currency = "TRY" } },
            simulated = true,
        });
    }

    private static SimResponse Timeout(TimeoutDefinition d, EngineInput input) =>
        Json(200, new { reportId = $"rep_sim_{input.Sequence:D6}", status = "ready", simulated = true }) with { DelayMs = d.DelayMs };

    public static SimResponse Json(int status, object body, params (string Name, string Value)[] headers) =>
        new(status, 0, JsonSerializer.Serialize(body, ScenarioJson.Options), headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));
}
