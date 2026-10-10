using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Options;
using APIArena.Application.Reports;
using APIArena.Application.Runs;
using APIArena.Domain.Entities;
using APIArena.Domain.Evaluation;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Webhooks;

public sealed record DeliveryDto(Guid DeliveryId, string EventId, string AggregateId, int EventVersion, string EventType, int PlannedOrder, int AttemptNumber,
    DateTimeOffset ScheduledAt, DateTimeOffset? SentAt, DateTimeOffset? CompletedAt, int? ResponseStatus, int? DurationMs, DeliveryAttemptOutcome Outcome, string? Error);

/// <summary>
/// Plans synthetic payment events when a webhook run starts (WH-09). Event and aggregate ids are server-generated;
/// a duplicate is the same EventId with a different DeliveryId. Planned order is stored separately from what actually
/// happened (SentAt), so the report can show both. Spacing respects the 2 req/s per-run budget.
/// </summary>
public sealed class WebhookRunPlanner(IAppDbContext db, TimeProvider clock, IOptions<WebhookOptions> options) : IRunLifecycleParticipant
{
    public async Task OnStartedAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
        if (!Engines.IsWebhook(run.Engine) || run.WebhookTargetId is null || await db.WebhookDeliveryAttempts.AnyAsync(d => d.RunId == runId, ct))
        {
            return;
        }

        using var config = JsonDocument.Parse(run.ConfigSnapshotJson);
        var definition = config.RootElement.GetProperty("definition").GetRawText();
        var now = clock.GetUtcNow();
        var start = now.AddSeconds(1);
        var key = runId.ToString("N")[..12];
        var aggregateId = $"pay_sim_{key}";
        var plan = new List<(string EventId, int Version, string Type, string Status, DateTimeOffset At)>();

        if (run.Engine == Engines.DuplicateWebhook)
        {
            var d = ScenarioJson.Deserialize<DuplicateWebhookDefinition>(definition);
            var copies = Math.Clamp(d.Copies, 2, options.Value.DeliveriesPerRun);
            for (var i = 0; i < copies; i++)
            {
                // Parallel: both copies are due at the same instant (2 requests in one second = the per-run budget).
                plan.Add(($"evt_sim_{key}_1", 1, d.EventType, "succeeded", d.Parallel ? start : start.AddMilliseconds(500 * i)));
            }

            await AddAsync(runId, run.WebhookTargetId.Value, aggregateId, plan, d.Amount, d.Currency, ct);
        }
        else
        {
            var d = ScenarioJson.Deserialize<OutOfOrderWebhookDefinition>(definition);
            plan.Add(($"evt_sim_{key}_v{d.LatestVersion}", d.LatestVersion, d.EventType, d.LatestStatus, start));
            plan.Add(($"evt_sim_{key}_v{d.StaleVersion}", d.StaleVersion, d.EventType, d.StaleStatus, start.AddMilliseconds(Math.Max(500, d.GapMs))));
            await AddAsync(runId, run.WebhookTargetId.Value, aggregateId, plan, d.Amount, d.Currency, ct);
        }
    }

    private async Task AddAsync(Guid runId, Guid targetId, string aggregateId, List<(string EventId, int Version, string Type, string Status, DateTimeOffset At)> plan,
        long amount, string currency, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var order = 0;
        foreach (var p in plan)
        {
            order++;
            var payload = JsonSerializer.Serialize(new
            {
                id = p.EventId,
                type = p.Type,
                runId,
                aggregateId,
                version = p.Version,
                data = new { paymentId = aggregateId, status = p.Status, amount, currency },
                synthetic = true,
                note = "API Arena synthetic event. Not a real payment.",
            });
            var delivery = new WebhookDeliveryAttempt
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                TargetId = targetId,
                EventId = p.EventId,
                AggregateId = aggregateId,
                EventVersion = p.Version,
                EventType = p.Type,
                PayloadJson = payload,
                PlannedOrder = order,
                AttemptNumber = 1,
                ScheduledAt = p.At,
                Outcome = DeliveryAttemptOutcome.Pending,
            };
            db.WebhookDeliveryAttempts.Add(delivery);
            db.OutboxJobs.Add(NewDeliveryJob(runId, delivery.Id, p.At, now));
        }

        await db.SaveChangesAsync(ct);
    }

    public static OutboxJob NewDeliveryJob(Guid runId, Guid deliveryId, DateTimeOffset at, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobTypes.DeliverWebhook,
        PayloadJson = JsonSerializer.Serialize(new { deliveryId }),
        RunId = runId,
        Status = OutboxStatus.Pending,
        MaxAttempts = 3,
        NextAttemptAt = at,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Run ended (finish/cancel/expire/deletion): pending deliveries and their jobs are cancelled.</summary>
    public Task OnEndedAsync(Guid runId, CancellationToken ct) =>
        WebhookTargetService.CancelPendingDeliveriesAsync(db, clock.GetUtcNow(), ct, runId: runId);
}

/// <summary>Defers webhook-run finalisation to the worker, which first fetches adapter evidence (network).</summary>
public sealed class WebhookFinalizationScheduler(IAppDbContext db, TimeProvider clock, IOptions<WebhookOptions> options) : IWebhookFinalizationScheduler
{
    public async Task<bool> TryScheduleAsync(Guid runId, CancellationToken ct)
    {
        if (!options.Value.Enabled)
        {
            return false; // module disabled: evaluate with recorded deliveries only (adapter evidence → Inconclusive)
        }

        if (!await db.OutboxJobs.AnyAsync(j => j.RunId == runId && j.Type == JobTypes.FinalizeWebhookRun && j.Status != OutboxStatus.Cancelled, ct))
        {
            var now = clock.GetUtcNow();
            db.OutboxJobs.Add(new OutboxJob
            {
                Id = Guid.NewGuid(),
                Type = JobTypes.FinalizeWebhookRun,
                PayloadJson = JsonSerializer.Serialize(new { runId }),
                RunId = runId,
                Status = OutboxStatus.Pending,
                MaxAttempts = 3,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    public async Task<IReadOnlyList<DeliveryFact>> LoadDeliveriesAsync(Guid runId, CancellationToken ct) =>
        (await db.WebhookDeliveryAttempts.AsNoTracking().Where(d => d.RunId == runId).OrderBy(d => d.PlannedOrder).ThenBy(d => d.AttemptNumber).ToListAsync(ct))
        .Select(d => new DeliveryFact(d.Id, d.EventId, d.AggregateId, d.EventVersion, d.PlannedOrder, d.AttemptNumber, d.SentAt, d.ResponseStatus, d.Outcome.ToString()))
        .ToList();
}

public sealed class DeliveryQueryService(IAppDbContext db, AccessScope scope)
{
    public async Task<IReadOnlyList<DeliveryDto>> ListAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        await scope.RequireRunAsync(userId, runId, ct);
        return await db.WebhookDeliveryAttempts.AsNoTracking().Where(d => d.RunId == runId)
            .OrderBy(d => d.PlannedOrder).ThenBy(d => d.AttemptNumber)
            .Select(d => new DeliveryDto(d.Id, d.EventId, d.AggregateId, d.EventVersion, d.EventType, d.PlannedOrder, d.AttemptNumber,
                d.ScheduledAt, d.SentAt, d.CompletedAt, d.ResponseStatus, d.DurationMs, d.Outcome, d.Error))
            .ToListAsync(ct);
    }
}
