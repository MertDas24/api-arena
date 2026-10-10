using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Application.Runs;

public sealed record ObservationSummary(
    Guid Id,
    long Sequence,
    string Method,
    string Route,
    string? OperationKey,
    int Attempt,
    DateTimeOffset ReceivedAt,
    int PlannedStatus,
    int PlannedDelayMs,
    DeliveryOutcome Outcome,
    int? DurationMs,
    string? RejectReason,
    bool IsSimulatedError);

public sealed record ObservationDetail(ObservationSummary Summary, JsonElement Headers, int BodyBytes, string? BodyPreview, bool BodyTruncated, bool BodyCaptureEnabled, string? ResponsePreview);

public sealed record FeedItem(long Sequence, string Type, DateTimeOffset OccurredAt, Guid? ObservationId, JsonElement? Data, ObservationSummary? Observation);

public sealed record FeedPage(IReadOnlyList<FeedItem> Items, long LastSequence, bool HasMore, RunStatus RunStatus, DateTimeOffset? ExpiresAt, int InboundCount, int InboundLimit, DateTimeOffset ServerTime);

/// <summary>Live feed and traffic detail. Every query is scoped through <see cref="AccessScope"/> (also on reconnect).</summary>
public sealed class ObservationService(IAppDbContext db, AccessScope scope, RunService runs, TimeProvider clock)
{
    public const int MaxFeedPage = 200;

    /// <summary>
    /// Append-only feed ordered by the run sequence. Clients resume with the last sequence they rendered, so a reconnect
    /// neither loses nor duplicates events. Outcome changes arrive as separate ResponseOutcome events.
    /// </summary>
    public async Task<FeedPage> FeedAsync(Guid userId, Guid runId, long afterSequence, int? limit, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        if (run.Status == RunStatus.Running && run.ExpiresAt <= clock.GetUtcNow())
        {
            await runs.TransitionAsync(run.Id, [RunStatus.Running], RunStatus.Expired, IncompleteReasons.Expired, ct);
        }

        var take = Math.Clamp(limit ?? 100, 1, MaxFeedPage);
        var rows = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && e.Sequence > Math.Max(0, afterSequence))
            .OrderBy(e => e.Sequence)
            .Take(take + 1)
            .GroupJoin(db.RequestObservations.AsNoTracking(), e => e.ObservationId, o => o.Id, (e, os) => new { e, os })
            .SelectMany(x => x.os.DefaultIfEmpty(), (x, o) => new { x.e, o })
            .ToListAsync(ct);

        var hasMore = rows.Count > take;
        var page = rows.Take(take).OrderBy(r => r.e.Sequence).Select(r => new FeedItem(
            r.e.Sequence,
            r.e.Type,
            r.e.OccurredAt,
            r.e.ObservationId,
            r.e.DataJson is null ? null : JsonDocument.Parse(r.e.DataJson).RootElement.Clone(),
            r.o is null ? null : ToSummary(r.o))).ToList();

        var current = await db.Runs.AsNoTracking().Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.ExpiresAt, r.InboundCount, r.InboundLimit }).FirstAsync(ct);
        return new FeedPage(page, page.Count > 0 ? page[^1].Sequence : afterSequence, hasMore, current.Status, current.ExpiresAt,
            current.InboundCount, current.InboundLimit, clock.GetUtcNow());
    }

    public static readonly string[] SortKeys = ["sequence"];

    public async Task<PagedResult<ObservationSummary>> ListAsync(Guid userId, Guid runId, PageRequest page, int? status, DeliveryOutcome? outcome, string? operationKey, CancellationToken ct)
    {
        await scope.RequireRunAsync(userId, runId, ct);
        var q = db.RequestObservations.AsNoTracking().Where(o => o.RunId == runId);
        if (status is { } s)
        {
            q = q.Where(o => o.PlannedStatus == s);
        }

        if (outcome is { } oc)
        {
            q = q.Where(o => o.Outcome == oc);
        }

        if (!string.IsNullOrEmpty(operationKey))
        {
            q = q.Where(o => o.OperationKey == operationKey);
        }

        var total = await q.CountAsync(ct);
        q = page.Descending ? q.OrderByDescending(o => o.Sequence) : q.OrderBy(o => o.Sequence);
        var items = await q.Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new PagedResult<ObservationSummary>(items.Select(ToSummary).ToList(), page.Page, page.PageSize, total);
    }

    public async Task<ObservationDetail> GetAsync(Guid userId, Guid runId, Guid observationId, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        var o = await db.RequestObservations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == observationId && x.RunId == runId, ct)
            ?? throw new NotFoundException("Gözlem");
        using var config = JsonDocument.Parse(run.ConfigSnapshotJson);
        var capture = config.RootElement.TryGetProperty("captureBodies", out var c) && c.GetBoolean();
        return new ObservationDetail(ToSummary(o), JsonDocument.Parse(o.HeadersJson).RootElement.Clone(), o.BodyBytes, o.BodyPreview, o.BodyTruncated, capture, o.ResponsePreview);
    }

    public static ObservationSummary ToSummary(RequestObservation o) => new(
        o.Id, o.Sequence, o.Method, o.Route, o.OperationKey, o.Attempt, o.ReceivedAt, o.PlannedStatus, o.PlannedDelayMs, o.Outcome, o.DurationMs, o.RejectReason,
        o.Outcome != DeliveryOutcome.Rejected && o.PlannedStatus >= 400);
}
