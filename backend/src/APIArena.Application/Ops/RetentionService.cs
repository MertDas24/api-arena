using System.ComponentModel.DataAnnotations;
using APIArena.Application.Abstractions;
using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Ops;

/// <summary>Retention defaults (assumptions, configurable; shown to users via /api/features).</summary>
public sealed class RetentionOptions
{
    public const string Section = "ApiArena:Retention";

    [Range(1, 365)] public int ObservationDays { get; set; } = 7;
    [Range(1, 3650)] public int ReportDays { get; set; } = 30;
    [Range(1, 90)] public int DeletedProjectDays { get; set; } = 7;
    [Range(1, 3650)] public int AuditDays { get; set; } = 90;
    [Range(1, 90)] public int JobHistoryDays { get; set; } = 7;
    [Range(10, 5000)] public int BatchSize { get; set; } = 500;
    [Range(1, 100)] public int MaxBatchesPerCategory { get; set; } = 20;
    [Range(5, 1440)] public int IntervalMinutes { get; set; } = 60;
}

public sealed record RetentionResult(int Observations, int Events, int Runs, int Projects, int Keys, int Shares, int Jobs, int Audit, int Sessions)
{
    public int Total => Observations + Events + Runs + Projects + Keys + Shares + Jobs + Audit + Sessions;
}

/// <summary>Deletes deleted-user-session rows: implemented in Infrastructure because sessions belong to Identity.</summary>
public interface ISessionCleanup
{
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int batch, CancellationToken ct);
}

/// <summary>
/// Idempotent, bounded retention (OPS-04): each category deletes at most BatchSize × MaxBatchesPerCategory rows per
/// pass, selecting ids first so every statement is small. Running it twice is harmless; the next pass continues.
/// Backups are not touched — deleted data can survive in backups until their own retention expires (documented).
/// </summary>
public sealed class RetentionService(IAppDbContext db, ISessionCleanup sessions, TimeProvider clock, IOptions<RetentionOptions> options)
{
    private RetentionOptions O => options.Value;

    public async Task<RetentionResult> CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var obsCutoff = now.AddDays(-O.ObservationDays);
        var reportCutoff = now.AddDays(-O.ReportDays);
        var terminal = new[] { RunStatus.Completed, RunStatus.Cancelled, RunStatus.Expired, RunStatus.Failed };

        // 1) Raw-ish traffic of finished runs (reports keep their own masked summary until ReportDays).
        var observations = await Batched(() => db.RequestObservations
            .Where(o => db.Runs.Any(r => r.Id == o.RunId && terminal.Contains(r.Status) && r.EndedAt < obsCutoff)).Select(o => o.Id),
            ids => db.RequestObservations.Where(o => ids.Contains(o.Id)).ExecuteDeleteAsync(ct), ct);
        var events = await Batched(() => db.RunEvents
            .Where(e => db.Runs.Any(r => r.Id == e.RunId && terminal.Contains(r.Status) && r.EndedAt < obsCutoff)).Select(e => e.Id),
            ids => db.RunEvents.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct), ct);
        await db.RunOperations.Where(o => db.Runs.Any(r => r.Id == o.RunId && terminal.Contains(r.Status) && r.EndedAt < obsCutoff)).ExecuteDeleteAsync(ct);

        // 2) Whole runs (report, checks, keys, shares, deliveries cascade) after ReportDays.
        var runs = await Batched(() => db.Runs.Where(r => terminal.Contains(r.Status) && r.EndedAt < reportCutoff).Select(r => r.Id),
            ids => db.Runs.Where(r => ids.Contains(r.Id)).ExecuteDeleteAsync(ct), ct);

        // 3) Soft-deleted projects past their grace period (cascade).
        var projectCutoff = now.AddDays(-O.DeletedProjectDays);
        var projects = await Batched(() => db.Projects.Where(p => p.DeletedAt != null && p.DeletedAt < projectCutoff).Select(p => p.Id),
            ids => db.Projects.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct), ct);

        // 4) Dead credentials: revoked/expired run keys and share links (no value after a day).
        var deadCutoff = now.AddDays(-1);
        var keys = await Batched(() => db.RunAccessKeys.Where(k => (k.RevokedAt != null && k.RevokedAt < deadCutoff) || k.ExpiresAt < deadCutoff).Select(k => k.Id),
            ids => db.RunAccessKeys.Where(k => ids.Contains(k.Id)).ExecuteDeleteAsync(ct), ct);
        var shares = await Batched(() => db.ShareLinks.Where(s => (s.RevokedAt != null && s.RevokedAt < deadCutoff) || s.ExpiresAt < deadCutoff).Select(s => s.Id),
            ids => db.ShareLinks.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct), ct);

        // 5) Finished/abandoned job history and old audit rows.
        var jobCutoff = now.AddDays(-O.JobHistoryDays);
        var jobs = await Batched(() => db.OutboxJobs.Where(j => (j.Status == OutboxStatus.Succeeded || j.Status == OutboxStatus.Failed || j.Status == OutboxStatus.Cancelled) && j.UpdatedAt < jobCutoff).Select(j => j.Id),
            ids => db.OutboxJobs.Where(j => ids.Contains(j.Id)).ExecuteDeleteAsync(ct), ct);
        var auditCutoff = now.AddDays(-O.AuditDays);
        var audit = await Batched(() => db.AuditEvents.Where(a => a.OccurredAt < auditCutoff).Select(a => a.Id),
            ids => db.AuditEvents.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct), ct);

        var expiredSessions = await sessions.DeleteExpiredAsync(now, O.BatchSize, ct);
        return new RetentionResult(observations, events, runs, projects, keys, shares, jobs, audit, expiredSessions);
    }

    private async Task<int> Batched<TKey>(Func<IQueryable<TKey>> select, Func<List<TKey>, Task<int>> delete, CancellationToken ct)
    {
        var total = 0;
        for (var i = 0; i < O.MaxBatchesPerCategory; i++)
        {
            var ids = await select().Take(O.BatchSize).ToListAsync(ct);
            if (ids.Count == 0)
            {
                break;
            }

            total += await delete(ids);
            if (ids.Count < O.BatchSize)
            {
                break;
            }
        }

        return total;
    }
}
