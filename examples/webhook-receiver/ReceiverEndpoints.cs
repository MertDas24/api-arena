using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApiArena.WebhookReceiver;

public sealed class ReceiverOptions
{
    /// <summary>Signing secret shown once by API Arena when the target is created (env RECEIVER_SIGNING_SECRET).</summary>
    public string SigningSecret { get; set; } = "";

    public int TimestampToleranceSeconds { get; set; } = 300;
}

/// <summary>
/// Sample receiver for API Arena v1 webhook scenarios. Mirrors the documented contract (docs/WEBHOOKS.md):
///   signature = v1=hex(HMAC-SHA256(secret, "{X-Arena-Timestamp}.{X-Arena-Delivery-Id}.{rawBody}")), constant-time compare,
///   timestamp within ±5 minutes, run binding (payload.runId == X-Arena-Run-Id), delivery-id replay protection,
///   event-id idempotency and version guard in <see cref="ReceiverStore"/>.
/// Duplicate and stale events are acknowledged with 200 as a no-op so the sender does not keep retrying them.
/// </summary>
public static class ReceiverEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app, ReceiverStore store, ReceiverOptions options, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        app.MapGet("/", () => Results.Ok(new { service = "apiarena-sample-receiver", naive = store.Naive }));

        app.MapPost("/webhooks/apiarena", async (HttpRequest request, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (!Authentic(request, body, options, clock, out var deliveryId))
            {
                return Results.Json(new { error = "invalid_signature_or_timestamp" }, statusCode: 401);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "apiarena.verification")
            {
                var challenge = doc.RootElement.GetProperty("challenge").GetString() ?? "";
                return Results.Json(new { challengeResponse = ChallengeResponse(options.SigningSecret, challenge) });
            }

            var evt = JsonSerializer.Deserialize<PaymentEvent>(body, Json);
            if (evt is null || evt.Synthetic is false)
            {
                return Results.Json(new { error = "unsupported_event" }, statusCode: 400);
            }

            if (!Guid.TryParse(request.Headers["X-Arena-Run-Id"], out var runHeader) || runHeader != evt.RunId)
            {
                return Results.Json(new { error = "run_binding_mismatch" }, statusCode: 400);
            }

            if (!await store.TryRecordDeliveryAsync(deliveryId, ct))
            {
                return Results.Ok(new { status = "duplicate-delivery-acknowledged" });
            }

            var result = await store.ApplyAsync(evt, ct);
            return Results.Ok(new { status = result.ToString() });
        });

        // Read-only adapter (C7): reports the synthetic run's state; never writes, never touches non-run data.
        app.MapPost("/webhooks/apiarena/adapter", async (HttpRequest request, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(request, ct);
            if (!Authentic(request, body, options, clock, out _))
            {
                return Results.Json(new { error = "invalid_signature_or_timestamp" }, statusCode: 401);
            }

            var query = JsonSerializer.Deserialize<AdapterQuery>(body, Json);
            if (query is null || query.Type != "apiarena.adapter.snapshot")
            {
                return Results.Json(new { error = "unsupported_query" }, statusCode: 400);
            }

            var aggregates = await store.AggregatesAsync(query.RunId, query.AggregateIds, ct);
            var events = new List<object>();
            foreach (var eventId in query.EventIds.Take(20))
            {
                var aggregate = query.AggregateIds.FirstOrDefault() ?? "";
                events.Add(new { eventId, processedCount = await store.AppliedCountAsync(query.RunId, eventId, aggregate, ct) });
            }

            return Results.Json(new
            {
                runId = query.RunId,
                nonce = query.Nonce,
                aggregates = aggregates.Select(a => new { aggregateId = a.AggregateId, status = a.Status, version = a.Version, amount = a.Amount }),
                events,
            });
        });
    }

    private sealed record AdapterQuery(string Type, Guid RunId, string Nonce, string[] AggregateIds, string[] EventIds);

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        return body.Length > 64 * 1024 ? "" : body;
    }

    private static bool Authentic(HttpRequest request, string body, ReceiverOptions options, TimeProvider clock, out string deliveryId)
    {
        deliveryId = request.Headers["X-Arena-Delivery-Id"].ToString();
        var signature = request.Headers["X-Arena-Signature"].ToString();
        if (string.IsNullOrEmpty(options.SigningSecret) || deliveryId.Length is 0 or > 80 || body.Length == 0
            || !long.TryParse(request.Headers["X-Arena-Timestamp"], NumberStyles.None, CultureInfo.InvariantCulture, out var ts))
        {
            return false;
        }

        if (Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - ts) > options.TimestampToleranceSeconds)
        {
            return false;
        }

        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.SigningSecret), Encoding.UTF8.GetBytes($"{ts}.{deliveryId}.{body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    public static string ChallengeResponse(string secret, string challenge) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("apiarena-verify." + challenge)));
}
