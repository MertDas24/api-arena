using APIArena.Domain.Entities;
using APIArena.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIArena.Infrastructure.Jobs;

public interface IJobHandler
{
    string Type { get; }

    /// <summary>Must be idempotent: a job can run again after a crash (lease expiry) — at-least-once semantics.</summary>
    Task HandleAsync(OutboxJob job, CancellationToken ct);
}

/// <summary>Thrown by handlers for transient failures that should be retried with backoff.</summary>
public sealed class RetryLaterException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Durable SQL outbox (WH-07). Claiming uses UPDATE TOP(n) … WITH (UPDLOCK, READPAST, ROWLOCK) so concurrent
/// workers never take the same job; each claim holds a lease, and a job whose lease expired (worker crash) becomes
/// claimable again. Semantics are at-least-once — never exactly-once.
/// </summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OutboxProcessor> logger)
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    public string WorkerId { get; } = Truncate($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", 80);

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    private sealed record Claimed(Guid Id);

    public async Task<int> RunOnceAsync(int batch, CancellationToken ct)
    {
        List<Guid> ids;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow();
            var until = now + Lease;
            ids = (await db.Database.SqlQuery<Claimed>($"""
                UPDATE TOP ({batch}) j WITH (UPDLOCK, READPAST, ROWLOCK)
                SET j.[Status] = 1, j.[LeaseOwner] = {WorkerId}, j.[LeaseUntil] = {until}, j.[Attempts] = j.[Attempts] + 1, j.[UpdatedAt] = {now}
                OUTPUT inserted.[Id] AS [Id]
                FROM [OutboxJobs] j
                WHERE (j.[Status] = 0 AND j.[NextAttemptAt] <= {now})
                   OR (j.[Status] = 1 AND j.[LeaseUntil] < {now})
                """).ToListAsync(ct)).Select(c => c.Id).ToList();
        }

        foreach (var id in ids)
        {
            await ProcessAsync(id, ct);
        }

        return ids.Count;
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.OutboxJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.LeaseOwner == WorkerId, ct);
        if (job is null)
        {
            return;
        }

        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        var now = clock.GetUtcNow();
        if (handler is null)
        {
            // Module disabled (e.g. webhooks flag off): leave the job untouched for a worker that can run it, but release the lease.
            await Finish(db, id, OutboxStatus.Pending, now.AddMinutes(5), "no_handler", ct, keepAttempts: false);
            return;
        }

        try
        {
            await handler.HandleAsync(job, ct);
            await Finish(db, id, OutboxStatus.Succeeded, now, null, ct);
            APIArena.Application.Ops.ArenaMetrics.Job(job.Type, "succeeded");
        }
        catch (RetryLaterException retry) when (job.Attempts < job.MaxAttempts)
        {
            await Finish(db, id, OutboxStatus.Pending, clock.GetUtcNow().AddSeconds(Math.Pow(2, job.Attempts)), retry.Code, ct);
            APIArena.Application.Ops.ArenaMetrics.Job(job.Type, "retry");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Outbox job {JobId} of type {Type} failed ({Error})", id, job.Type, ex is RetryLaterException r ? r.Code : ex.GetType().Name);
            await Finish(db, id, OutboxStatus.Failed, clock.GetUtcNow(), ex is RetryLaterException rl ? rl.Code : ex.GetType().Name, ct);
            APIArena.Application.Ops.ArenaMetrics.Job(job.Type, "failed");
        }
    }

    private async Task Finish(AppDbContext db, Guid id, OutboxStatus status, DateTimeOffset next, string? error, CancellationToken ct, bool keepAttempts = true)
    {
        var now = clock.GetUtcNow();
        var trimmed = error is { Length: > 200 } ? error[..200] : error;
        // Only the lease owner may complete the job; a cancelled job stays cancelled.
        await db.OutboxJobs.Where(j => j.Id == id && j.LeaseOwner == WorkerId && j.Status == OutboxStatus.Leased)
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.NextAttemptAt, next)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, trimmed)
                .SetProperty(j => j.Attempts, j => keepAttempts ? j.Attempts : j.Attempts - 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }
}

/// <summary>Worker loop around <see cref="OutboxProcessor"/>; resilient to transient DB errors.</summary>
public sealed class OutboxWorker(OutboxProcessor processor, TimeProvider clock, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await processor.RunOnceAsync(10, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Outbox loop error ({Error})", ex.GetType().Name);
            }

            if (processed == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
