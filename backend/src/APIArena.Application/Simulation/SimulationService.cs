using System.Data;
using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Application.Simulation;

/// <summary>What the simulator knows about an authenticated run (definition comes from the run's immutable snapshot).</summary>
public sealed record RunContext(Guid RunId, Guid ProjectId, string Engine, string DefinitionJson, bool CaptureBodies, IReadOnlyList<string> AllowedOrigins, FixtureRoute? Route);

public sealed record InboundRequest(
    string Method,
    string Route,
    DateTimeOffset ReceivedAt,
    string? OperationKey,
    string HeadersJson,
    int BodyBytes,
    string? BodyPreview,
    bool BodyTruncated);

public enum PlanStatus
{
    Planned,
    QuotaExceeded,
    NotRunning,
    Expired,
}

public sealed record PlanResult(PlanStatus Status, Guid ObservationId, long Sequence, int Attempt, SimResponse? Response);

public sealed class SimulationService(IAppDbContext db, IRunAtomics atomics, TimeProvider clock)
{
    public const string AnonymousOperation = "(no-idempotency-key)";

    /// <summary>
    /// Resolves the run for a presented key: hash lookup bound to the run id in the URL; revoked/expired keys fail.
    /// Callers must check the key's shape first so malformed credentials never reach the database.
    /// </summary>
    public async Task<RunContext?> AuthenticateAsync(Guid runId, string key, CancellationToken ct)
    {
        var hash = RunKeys.Hash(key);
        var now = clock.GetUtcNow();
        var row = await db.RunAccessKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RunId == runId && k.RevokedAt == null && k.ExpiresAt > now)
            .Join(db.Runs, k => k.RunId, r => r.Id, (k, r) => r)
            .Join(db.Projects.Where(p => p.DeletedAt == null), r => r.ProjectId, p => p.Id, (r, p) => new { r.Id, r.ProjectId, r.Engine, r.ConfigSnapshotJson, p.SimulatorAllowedOrigins })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToContext(row.Id, row.ProjectId, row.Engine, row.ConfigSnapshotJson, row.SimulatorAllowedOrigins);
    }

    /// <summary>CORS preflight carries no credentials; only the project's explicit origin list and the fixture route are disclosed.</summary>
    public async Task<RunContext?> GetForPreflightAsync(Guid runId, CancellationToken ct)
    {
        var row = await db.Runs.AsNoTracking().Where(r => r.Id == runId && r.Status == RunStatus.Running)
            .Join(db.Projects.Where(p => p.DeletedAt == null), r => r.ProjectId, p => p.Id, (r, p) => new { r.Id, r.ProjectId, r.Engine, r.ConfigSnapshotJson, p.SimulatorAllowedOrigins })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToContext(row.Id, row.ProjectId, row.Engine, row.ConfigSnapshotJson, row.SimulatorAllowedOrigins);
    }

    private static RunContext ToContext(Guid id, Guid projectId, string engine, string snapshot, string origins)
    {
        using var doc = JsonDocument.Parse(snapshot);
        var definition = doc.RootElement.GetProperty("definition").GetRawText();
        var capture = doc.RootElement.TryGetProperty("captureBodies", out var c) && c.GetBoolean();
        return new RunContext(id, projectId, engine, definition, capture,
            origins.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ScenarioEngine.RouteOf(engine, definition));
    }

    /// <summary>
    /// One short transaction: quota/status check + sequence (single UPDATE), per-operation attempt (MERGE), pure decision,
    /// observation (Planned) + RequestReceived event. No network wait happens while it is open.
    /// </summary>
    public async Task<PlanResult> PlanAsync(RunContext run, InboundRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var window = ScenarioEngine.RateLimitWindowIfFirst(run.Engine, run.DefinitionJson, request.ReceivedAt);
        var (allocation, rejection) = await atomics.AllocateInboundAsync(run.RunId, clock.GetUtcNow(), window, ct);
        if (allocation is null)
        {
            await tx.CommitAsync(ct); // persists QuotaExceeded flag
            if (rejection == InboundRejection.QuotaExceeded)
            {
                await AppendOnceAsync(run.RunId, "QuotaReached", ct);
            }

            return new PlanResult(rejection switch
            {
                InboundRejection.QuotaExceeded => PlanStatus.QuotaExceeded,
                InboundRejection.Expired => PlanStatus.Expired,
                _ => PlanStatus.NotRunning,
            }, Guid.Empty, 0, 0, null);
        }

        var attempt = await atomics.IncrementOperationAsync(run.RunId, request.OperationKey ?? AnonymousOperation, request.ReceivedAt, ct);
        var response = ScenarioEngine.Decide(run.Engine, run.DefinitionJson,
            new EngineInput(allocation.Sequence, attempt, request.ReceivedAt, allocation.PreviousRateLimitedUntil));

        var observation = NewObservation(run.RunId, allocation.Sequence, attempt, request, response.Status, response.DelayMs, DeliveryOutcome.Planned, null);
        observation.ResponsePreview = Truncate(response.Body, 1000);
        db.RequestObservations.Add(observation);
        db.RunEvents.Add(new RunEvent
        {
            RunId = run.RunId,
            Sequence = allocation.Sequence,
            Type = "RequestReceived",
            OccurredAt = request.ReceivedAt,
            ObservationId = observation.Id,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new PlanResult(PlanStatus.Planned, observation.Id, allocation.Sequence, attempt, response);
    }

    /// <summary>Records a request that reached a running run but was refused before a scenario decision (wrong route, too large, bad key header).</summary>
    public async Task<PlanStatus> RecordRejectedAsync(RunContext run, InboundRequest request, int status, string reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var (allocation, rejection) = await atomics.AllocateInboundAsync(run.RunId, clock.GetUtcNow(), null, ct);
        if (allocation is null)
        {
            await tx.CommitAsync(ct);
            return rejection == InboundRejection.QuotaExceeded ? PlanStatus.QuotaExceeded : rejection == InboundRejection.Expired ? PlanStatus.Expired : PlanStatus.NotRunning;
        }

        var observation = NewObservation(run.RunId, allocation.Sequence, 0, request, status, 0, DeliveryOutcome.Rejected, reason);
        observation.CompletedAt = request.ReceivedAt;
        observation.DurationMs = 0;
        db.RequestObservations.Add(observation);
        db.RunEvents.Add(new RunEvent
        {
            RunId = run.RunId,
            Sequence = allocation.Sequence,
            Type = "RequestRejected",
            OccurredAt = request.ReceivedAt,
            ObservationId = observation.Id,
            DataJson = JsonSerializer.Serialize(new { reason, status }),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PlanStatus.Planned;
    }

    /// <summary>Records what actually happened on the wire. Runs with CancellationToken.None by design (the client may be gone).</summary>
    public async Task CompleteAsync(Guid runId, Guid observationId, DeliveryOutcome outcome, int durationMs, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // CompletedAt == null covers Planned and the "Unknown" placeholder a concurrent finalisation may have set:
        // the observed wire outcome is more truthful. An already-generated report keeps what was known at that time.
        await db.RequestObservations.Where(o => o.Id == observationId && o.CompletedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(o => o.Outcome, outcome)
                .SetProperty(o => o.DurationMs, durationMs)
                .SetProperty(o => o.CompletedAt, now), ct);
        var seq = await atomics.AllocateSequenceAsync(runId, ct);
        db.RunEvents.Add(new RunEvent
        {
            RunId = runId,
            Sequence = seq,
            Type = "ResponseOutcome",
            OccurredAt = now,
            ObservationId = observationId,
            DataJson = JsonSerializer.Serialize(new { outcome = outcome.ToString(), durationMs }),
        });
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> IsRunningAsync(Guid runId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Runs.AnyAsync(r => r.Id == runId && r.Status == RunStatus.Running && r.ExpiresAt > now, ct);
    }

    private async Task AppendOnceAsync(Guid runId, string type, CancellationToken ct)
    {
        if (await db.RunEvents.AnyAsync(e => e.RunId == runId && e.Type == type, ct))
        {
            return;
        }

        var seq = await atomics.AllocateSequenceAsync(runId, ct);
        db.RunEvents.Add(new RunEvent { RunId = runId, Sequence = seq, Type = type, OccurredAt = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // concurrent duplicate notice is harmless
        }
    }

    private static RequestObservation NewObservation(Guid runId, long sequence, int attempt, InboundRequest r, int status, int delay, DeliveryOutcome outcome, string? reason) => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        Sequence = sequence,
        OperationKey = r.OperationKey,
        Attempt = attempt,
        Method = r.Method,
        Route = Truncate(r.Route, 200),
        ReceivedAt = r.ReceivedAt,
        PlannedStatus = status,
        PlannedDelayMs = delay,
        Outcome = outcome,
        HeadersJson = r.HeadersJson,
        BodyBytes = r.BodyBytes,
        BodyPreview = r.BodyPreview,
        BodyTruncated = r.BodyTruncated,
        RejectReason = reason,
    };

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
