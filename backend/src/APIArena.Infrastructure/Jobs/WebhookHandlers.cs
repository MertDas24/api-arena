using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using APIArena.Application.Options;
using APIArena.Application.Reports;
using APIArena.Application.Webhooks;
using APIArena.Domain.Entities;
using APIArena.Domain.Evaluation;
using APIArena.Domain.Outbound;
using APIArena.Infrastructure.Outbound;
using APIArena.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure.Jobs;

internal static class Signed
{
    public static Dictionary<string, string> Headers(string secret, string deliveryId, string body, DateTimeOffset now, string? eventId = null, Guid? runId = null)
    {
        var ts = now.ToUnixTimeSeconds();
        var headers = new Dictionary<string, string>
        {
            [WebhookSignature.TimestampHeader] = ts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [WebhookSignature.DeliveryIdHeader] = deliveryId,
            [WebhookSignature.Header] = WebhookSignature.Compute(WebhookSignature.SecretBytes(secret), ts, deliveryId, body),
        };
        if (eventId is not null)
        {
            headers[WebhookSignature.EventIdHeader] = eventId;
        }

        if (runId is not null)
        {
            headers[WebhookSignature.RunIdHeader] = runId.Value.ToString();
        }

        return headers;
    }

    public static string Nonce()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToHexStringLower(b);
    }
}

/// <summary>Ownership proof (WH-05): the target must answer a fresh random challenge with HMAC(secret, challenge).</summary>
internal sealed class VerifyTargetHandler(AppDbContext db, SafeHttpClient http, ISigningSecretStore secrets, AuditLog audit, TimeProvider clock, IOptions<WebhookOptions> options) : IJobHandler
{
    public string Type => JobTypes.VerifyTarget;

    private sealed record Payload(Guid TargetId, Guid? RequestedBy);

    private sealed record ChallengeAnswer([property: JsonPropertyName("challengeResponse")] string? ChallengeResponse);

    public async Task HandleAsync(OutboxJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonSerializerOptions.Web)!;
        var target = await db.WebhookTargets.FirstOrDefaultAsync(t => t.Id == payload.TargetId && t.RevokedAt == null, ct);
        if (target is null)
        {
            return;
        }

        var secret = secrets.Reveal(target.SigningSecretCiphertext, target.SigningSecretKeyId);
        var challenge = Signed.Nonce() + Signed.Nonce();
        var body = JsonSerializer.Serialize(new { type = "apiarena.verification", challenge, targetId = target.Id });
        var now = clock.GetUtcNow();
        var result = await http.PostJsonAsync(target.Url, body, Signed.Headers(secret, "verify-" + Guid.NewGuid().ToString("N"), body, now), ct);

        string? error = result.Error;
        if (error is null)
        {
            try
            {
                var answer = JsonSerializer.Deserialize<ChallengeAnswer>(result.Body ?? "", JsonSerializerOptions.Web);
                error = WebhookSignature.VerifyChallenge(WebhookSignature.SecretBytes(secret), challenge, answer?.ChallengeResponse) ? null : "challenge_mismatch";
            }
            catch (JsonException)
            {
                error = "challenge_invalid_response";
            }
        }

        var done = clock.GetUtcNow();
        target.LastVerificationError = error;
        target.UpdatedAt = done;
        if (error is null)
        {
            target.Status = WebhookTargetStatus.Verified;
            target.VerifiedAt = done;
            target.VerificationExpiresAt = done.AddDays(options.Value.VerificationValidityDays);
        }
        else
        {
            target.Status = WebhookTargetStatus.VerificationFailed;
            target.VerifiedAt = null;
            target.VerificationExpiresAt = null;
        }

        audit.Write(payload.RequestedBy, error is null ? "webhook_target.verified" : "webhook_target.verification_failed", "webhook_target", target.Id.ToString(), new { code = error });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // URL/secret changed while verifying: that change already reset verification; this result is stale.
        }
    }
}

/// <summary>
/// Delivers one attempt. SentAt is recorded before the network call; if the worker crashes after sending but before
/// recording the result, the job is retried with the SAME delivery id (receivers de-duplicate) — at-least-once.
/// Failed transient attempts schedule a new attempt (new delivery id, same event id) with bounded backoff.
/// </summary>
internal sealed class DeliverWebhookHandler(AppDbContext db, SafeHttpClient http, ISigningSecretStore secrets, TimeProvider clock, IOptions<WebhookOptions> options) : IJobHandler
{
    public string Type => JobTypes.DeliverWebhook;

    private sealed record Payload(Guid DeliveryId);

    public async Task HandleAsync(OutboxJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonSerializerOptions.Web)!;
        var delivery = await db.WebhookDeliveryAttempts.FirstOrDefaultAsync(d => d.Id == payload.DeliveryId, ct);
        if (delivery is null || delivery.Outcome != DeliveryAttemptOutcome.Pending)
        {
            return; // already finished or cancelled → idempotent no-op
        }

        var now = clock.GetUtcNow();
        var run = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == delivery.RunId, ct);
        var target = await db.WebhookTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == delivery.TargetId, ct);
        if (run.Status != RunStatus.Running || run.ExpiresAt <= now)
        {
            await Complete(delivery, DeliveryAttemptOutcome.Cancelled, null, null, "run_not_running", ct);
            return;
        }

        if (target is null || target.RevokedAt is not null || target.Status != WebhookTargetStatus.Verified || target.VerificationExpiresAt <= now)
        {
            await Complete(delivery, DeliveryAttemptOutcome.Blocked, null, null, "target_not_verified", ct);
            return;
        }

        delivery.SentAt ??= now;
        await db.SaveChangesAsync(ct);

        var secret = secrets.Reveal(target.SigningSecretCiphertext, target.SigningSecretKeyId);
        var result = await http.PostJsonAsync(target.Url, delivery.PayloadJson,
            Signed.Headers(secret, delivery.Id.ToString(), delivery.PayloadJson, now, delivery.EventId, delivery.RunId), ct);

        var outcome = result.Success ? DeliveryAttemptOutcome.Delivered
            : result.Error is "timeout" ? DeliveryAttemptOutcome.TimedOut
            : result.Error is "blocked_address" or "dns_failed" or "header_not_allowed" || (result.Error?.StartsWith("url_", StringComparison.Ordinal) ?? false) ? DeliveryAttemptOutcome.Blocked
            : DeliveryAttemptOutcome.Failed;
        delivery.DurationMs = result.DurationMs;
        APIArena.Application.Ops.ArenaMetrics.WebhookDelivery(outcome.ToString());
        await Complete(delivery, outcome, result.Status, result.DurationMs, result.Error, ct);

        var retryable = outcome is DeliveryAttemptOutcome.TimedOut || (outcome == DeliveryAttemptOutcome.Failed && (result.Status is null or >= 500 or 429));
        if (retryable && delivery.AttemptNumber < options.Value.MaxAttemptsPerDelivery)
        {
            var at = clock.GetUtcNow().AddSeconds(2 * delivery.AttemptNumber);
            var next = new WebhookDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                RunId = delivery.RunId,
                TargetId = delivery.TargetId,
                EventId = delivery.EventId,
                AggregateId = delivery.AggregateId,
                EventVersion = delivery.EventVersion,
                EventType = delivery.EventType,
                PayloadJson = delivery.PayloadJson,
                PlannedOrder = delivery.PlannedOrder,
                AttemptNumber = delivery.AttemptNumber + 1,
                ScheduledAt = at,
                Outcome = DeliveryAttemptOutcome.Pending,
            };
            db.WebhookDeliveryAttempts.Add(next);
            db.OutboxJobs.Add(WebhookRunPlanner.NewDeliveryJob(delivery.RunId, next.Id, at, clock.GetUtcNow()));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Unique (run, plannedOrder, attempt): a concurrent duplicate execution already scheduled it.
            }
        }
    }

    private async Task Complete(WebhookDeliveryAttempt delivery, DeliveryAttemptOutcome outcome, int? status, int? duration, string? error, CancellationToken ct)
    {
        delivery.Outcome = outcome;
        delivery.ResponseStatus = status;
        delivery.DurationMs = duration ?? delivery.DurationMs;
        delivery.CompletedAt = clock.GetUtcNow();
        delivery.Error = error is { Length: > 60 } ? error[..60] : error;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Fetches the read-only adapter snapshot (if configured) through the same SafeHttpClient, then finalises.</summary>
internal sealed class FinalizeWebhookRunHandler(AppDbContext db, SafeHttpClient http, ISigningSecretStore secrets, RunFinalizer finalizer,
    IWebhookFinalizationScheduler scheduler, TimeProvider clock) : IJobHandler
{
    public string Type => JobTypes.FinalizeWebhookRun;

    private sealed record Payload(Guid RunId);

    private sealed record AdapterResponse(Guid RunId, string Nonce, IReadOnlyList<AdapterAggregate> Aggregates, IReadOnlyList<AdapterEventCount> Events);

    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public async Task HandleAsync(OutboxJob job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<Payload>(job.PayloadJson, JsonSerializerOptions.Web)!;
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == payload.RunId, ct);
        if (run is null || await db.ReportSnapshots.AnyAsync(s => s.RunId == run.Id, ct))
        {
            return;
        }

        var deliveries = await scheduler.LoadDeliveriesAsync(run.Id, ct);
        var target = run.WebhookTargetId is { } tid ? await db.WebhookTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid, ct) : null;
        AdapterSnapshot? adapter = null;
        string? adapterError = null;
        if (target?.AdapterPath is { } path && target.RevokedAt is null && target.Status == WebhookTargetStatus.Verified)
        {
            (adapter, adapterError) = await FetchAdapterAsync(run.Id, target, path, deliveries, ct);
        }

        await finalizer.FinalizeAsync(run.Id, new WebhookEvidence(deliveries, adapter, adapterError), ct);
    }

    private async Task<(AdapterSnapshot?, string?)> FetchAdapterAsync(Guid runId, WebhookTarget target, string path, IReadOnlyList<DeliveryFact> deliveries, CancellationToken ct)
    {
        // Same origin as the verified target only (no separate, unverified host).
        var url = new Uri(new Uri(target.Url), path).AbsoluteUri;
        var nonce = Signed.Nonce();
        var body = JsonSerializer.Serialize(new
        {
            type = "apiarena.adapter.snapshot",
            runId,
            nonce,
            aggregateIds = deliveries.Select(d => d.AggregateId).Distinct().Take(20),
            eventIds = deliveries.Select(d => d.EventId).Distinct().Take(20),
        });
        var secret = secrets.Reveal(target.SigningSecretCiphertext, target.SigningSecretKeyId);
        var result = await http.PostJsonAsync(url, body, Signed.Headers(secret, "adapter-" + nonce, body, clock.GetUtcNow(), runId: runId), ct);
        if (!result.Success)
        {
            return (null, "adapter_" + (result.Error ?? "failed"));
        }

        try
        {
            var r = JsonSerializer.Deserialize<AdapterResponse>(result.Body ?? "", Strict);
            if (r is null || r.RunId != runId || r.Nonce != nonce)
            {
                return (null, "adapter_binding_mismatch");
            }

            if (r.Aggregates is null || r.Events is null || r.Aggregates.Count > 50 || r.Events.Count > 50)
            {
                return (null, "adapter_invalid_response");
            }

            return (new AdapterSnapshot(r.Aggregates, r.Events), null);
        }
        catch (JsonException)
        {
            return (null, "adapter_invalid_response");
        }
    }
}
