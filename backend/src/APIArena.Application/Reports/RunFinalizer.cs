using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Options;
using APIArena.Application.Runs;
using APIArena.Domain.Entities;
using APIArena.Domain.Evaluation;
using APIArena.Domain.Privacy;
using APIArena.Domain.Runs;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Reports;

/// <summary>Extra evidence for webhook runs (deliveries from the DB, adapter snapshot fetched by the worker).</summary>
public sealed record WebhookEvidence(IReadOnlyList<DeliveryFact> Deliveries, AdapterSnapshot? Adapter, string? AdapterError);

/// <summary>Webhook runs need a network call (adapter) before evaluation, which must not happen inside an API request.</summary>
public interface IWebhookFinalizationScheduler
{
    /// <summary>Returns true if finalisation was deferred to the worker.</summary>
    Task<bool> TryScheduleAsync(Guid runId, CancellationToken ct);

    Task<IReadOnlyList<DeliveryFact>> LoadDeliveriesAsync(Guid runId, CancellationToken ct);
}

public sealed class RunFinalizer(
    IAppDbContext db,
    TimeProvider clock,
    IOptions<LimitOptions> limits,
    ILogger<RunFinalizer> logger,
    IWebhookFinalizationScheduler? webhooks = null) : IRunFinalizer
{
    public const string Notice =
        "Bu rapor yalnızca platformun gözleyebildiği HTTP trafiğine dayanır. Inconclusive sonuçlar başarı sayılmaz; genel bir dayanıklılık puanı verilmez.";

    public async Task FinalizeAsync(Guid runId, CancellationToken ct) => await FinalizeAsync(runId, null, ct);

    /// <summary>Idempotent: the unique index on ReportSnapshots.RunId guarantees one immutable report per run.</summary>
    public async Task FinalizeAsync(Guid runId, WebhookEvidence? evidence, CancellationToken ct)
    {
        if (await db.ReportSnapshots.AnyAsync(s => s.RunId == runId, ct))
        {
            return;
        }

        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || !RunStateMachine.IsTerminal(run.Status))
        {
            return;
        }

        if (Engines.IsWebhook(run.Engine) && evidence is null && webhooks is not null && await webhooks.TryScheduleAsync(runId, ct))
        {
            return; // the worker fetches adapter evidence and calls back with it
        }

        // Anything still "Planned" never got a confirmed outcome: it is Unknown from now on.
        await db.RequestObservations.Where(o => o.RunId == runId && o.Outcome == DeliveryOutcome.Planned)
            .ExecuteUpdateAsync(u => u.SetProperty(o => o.Outcome, DeliveryOutcome.Unknown), ct);

        var observations = await db.RequestObservations.AsNoTracking().Where(o => o.RunId == runId).OrderBy(o => o.Sequence).ToListAsync(ct);
        var meta = await db.Projects.AsNoTracking().Where(p => p.Id == run.ProjectId).Select(p => new { p.Name }).FirstAsync(ct);
        var version = await db.ScenarioVersions.AsNoTracking().Where(v => v.Id == run.ScenarioVersionId)
            .Select(v => new { v.ContentJson, Title = db.ScenarioTemplates.Where(t => t.Id == v.TemplateId).Select(t => t.Title).First() }).FirstAsync(ct);

        var incomplete = run.IncompleteReason ?? (run.QuotaExceeded ? IncompleteReasons.QuotaExceeded : null);
        using var config = JsonDocument.Parse(run.ConfigSnapshotJson);
        var definition = config.RootElement.GetProperty("definition").GetRawText();

        var deliveries = evidence?.Deliveries ?? (Engines.IsWebhook(run.Engine) && webhooks is not null ? await webhooks.LoadDeliveriesAsync(runId, ct) : []);
        EvaluationResult result;
        try
        {
            result = Evaluator.Evaluate(new EvaluationInput(run.Engine, definition, run.EvaluatorVersion, run.Status, incomplete,
                observations.Select(o => new ObservationFact(o.Sequence, o.OperationKey, o.Attempt, o.ReceivedAt, o.PlannedStatus, o.Outcome, o.DurationMs, o.PlannedDelayMs, o.RejectReason)).ToList(),
                deliveries, evidence?.Adapter, evidence?.AdapterError));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A broken definition must never yield a "passed" report: record a platform failure instead.
            logger.LogError(ex, "Evaluation failed for run {RunId}", runId);
            result = new EvaluationResult(run.EvaluatorVersion, [], IncompleteReasons.PlatformFailure);
        }

        var checks = result.Checks.Select(c => new ReportCheck(c.Id, c.Title, c.Status, c.EvidenceKind, c.Expected, c.Observed, c.Explanation, c.Suggestion, c.Evidence)).ToList();
        var counts = new ReportCounts(
            checks.Count(c => c.Status == CheckStatus.Passed),
            checks.Count(c => c.Status == CheckStatus.Failed),
            checks.Count(c => c.Status == CheckStatus.Inconclusive),
            checks.Count(c => c.Status == CheckStatus.NotApplicable));

        var content = JsonSerializer.Deserialize<ScenarioContent>(version.ContentJson, ScenarioJson.Options)!;
        var unverifiable = content.Unobservable.ToList();
        if (Engines.IsWebhook(run.Engine))
        {
            unverifiable.Add("HTTP 200 onayı, olayın iş mantığında tek kez uygulandığının kanıtı değildir.");
            if (evidence?.Adapter is not null)
            {
                unverifiable.Add("Adapter sonuçları kullanıcının sistemi tarafından bildirilir; platform bunların doğruluğunu bağımsız olarak doğrulayamaz.");
            }
        }

        var max = limits.Value.MaxExportObservations;
        var start = run.StartedAt ?? run.CreatedAt;
        var timeline = observations.Take(max).Select(o => new TimelineEntry(o.Sequence, o.ReceivedAt, (long)(o.ReceivedAt - start).TotalMilliseconds, o.Method, o.Route, o.Attempt,
            o.OperationKey is null ? null : Masking.MaskString(o.OperationKey), o.PlannedStatus, o.Outcome, o.DurationMs, o.RejectReason)).ToList();

        var now = clock.GetUtcNow();
        var reportId = Guid.NewGuid();
        var finalIncomplete = result.IncompleteReason ?? incomplete;
        var document = new ReportDocument(reportId, run.Id, run.ProjectId, meta.Name, run.ScenarioSlug, run.ScenarioVersionNumber, version.Title, run.Engine, run.EvaluatorVersion,
            run.Status, run.Label, run.CreatedAt, run.StartedAt, run.EndedAt,
            run.StartedAt is { } s && run.EndedAt is { } e ? (long)(e - s).TotalMilliseconds : null,
            finalIncomplete, finalIncomplete is null ? null : Evaluator.ReasonText(finalIncomplete), now, counts,
            observations.Count(o => o.Outcome != DeliveryOutcome.Rejected), observations.Count(o => o.Outcome == DeliveryOutcome.Rejected), run.InboundLimit,
            checks, unverifiable, timeline, observations.Count > max,
            deliveries.Select(d => new DeliveryEntry(d.DeliveryId, d.EventId, d.EventVersion, d.PlannedOrder, d.AttemptNumber, d.SentAt, d.ResponseStatus, d.Outcome)).ToList(),
            evidence?.Adapter is not null, Notice);

        var snapshot = new ReportSnapshot
        {
            Id = reportId,
            RunId = run.Id,
            ScenarioSlug = run.ScenarioSlug,
            ScenarioVersion = run.ScenarioVersionNumber,
            EvaluatorVersion = run.EvaluatorVersion,
            RunFinalStatus = run.Status,
            IncompleteReason = finalIncomplete,
            GeneratedAt = now,
            Passed = counts.Passed,
            Failed = counts.Failed,
            Inconclusive = counts.Inconclusive,
            NotApplicable = counts.NotApplicable,
            RequestCount = document.RequestCount,
            ReportJson = JsonSerializer.Serialize(document, ScenarioJson.Options),
            Checks = result.Checks.Select((c, i) => new CheckResult
            {
                Id = Guid.NewGuid(),
                ReportId = reportId,
                CheckId = c.Id,
                Title = c.Title,
                Status = c.Status,
                EvidenceKind = c.EvidenceKind,
                Expected = Trim(c.Expected, 1000),
                Observed = Trim(c.Observed, 1000),
                Explanation = Trim(c.Explanation, 2000),
                Suggestion = c.Suggestion is null ? null : Trim(c.Suggestion, 2000),
                EvidenceJson = JsonSerializer.Serialize(c.Evidence.Take(100)),
                EvaluatorVersion = run.EvaluatorVersion,
                Order = i,
            }).ToList(),
        };

        db.ReportSnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(ct);
            if (run.IncompleteReason is null && finalIncomplete is not null)
            {
                await db.Runs.Where(r => r.Id == runId && r.IncompleteReason == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.IncompleteReason, finalIncomplete), ct);
            }
        }
        catch (DbUpdateException)
        {
            // Concurrent finalisation won the unique index — the existing snapshot is authoritative.
            ((DbContext)db).Entry(snapshot).State = EntityState.Detached;
            foreach (var c in snapshot.Checks)
            {
                ((DbContext)db).Entry(c).State = EntityState.Detached;
            }
        }
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
