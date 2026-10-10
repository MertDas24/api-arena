using System.Text.Json;
using APIArena.Application.Ops;
using APIArena.Application.Webhooks;
using APIArena.Domain.Entities;
using APIArena.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure.Jobs;

internal sealed class SessionCleanup(AppDbContext db) : ISessionCleanup
{
    public async Task<int> DeleteExpiredAsync(DateTimeOffset now, int batch, CancellationToken ct)
    {
        var ids = await db.UserSessions.Where(s => s.ExpiresAt < now).Select(s => s.Id).Take(batch).ToListAsync(ct);
        return ids.Count == 0 ? 0 : await db.UserSessions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Retention runs as a leased outbox job, so only one worker executes a given pass and a crash simply retries it.</summary>
internal sealed class RetentionHandler(RetentionService retention, ILogger<RetentionHandler> logger) : IJobHandler
{
    public string Type => JobTypes.RetentionCleanup;

    public async Task HandleAsync(OutboxJob job, CancellationToken ct)
    {
        var result = await retention.CleanupAsync(ct);
        if (result.Total > 0)
        {
            logger.LogInformation("Retention removed {Total} rows (observations {Obs}, runs {Runs}, projects {Projects})",
                result.Total, result.Observations, result.Runs, result.Projects);
        }
    }
}

/// <summary>Enqueues a retention pass every interval (if none is pending) and samples gauges for metrics.</summary>
public sealed class OpsScheduler(IServiceScopeFactory scopes, TimeProvider clock, IOptions<RetentionOptions> options, ILogger<OpsScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastRetention = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = clock.GetUtcNow();
                if (now - lastRetention >= TimeSpan.FromMinutes(options.Value.IntervalMinutes))
                {
                    await EnqueueRetentionAsync(db, now, stoppingToken);
                    lastRetention = now;
                }

                var active = await db.Runs.CountAsync(r => r.Status == RunStatus.Running && r.ExpiresAt > now, stoppingToken);
                var oldest = await db.OutboxJobs.Where(j => j.Status == OutboxStatus.Pending && j.NextAttemptAt <= now)
                    .OrderBy(j => j.NextAttemptAt).Select(j => (DateTimeOffset?)j.NextAttemptAt).FirstOrDefaultAsync(stoppingToken);
                ArenaMetrics.Sample(active, oldest is { } o ? Math.Max(0, (now - o).TotalSeconds) : 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Ops scheduler iteration failed ({Error})", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    public static async Task EnqueueRetentionAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.OutboxJobs.AnyAsync(j => j.Type == JobTypes.RetentionCleanup && (j.Status == OutboxStatus.Pending || j.Status == OutboxStatus.Leased), ct))
        {
            return;
        }

        db.OutboxJobs.Add(new OutboxJob
        {
            Id = Guid.NewGuid(),
            Type = JobTypes.RetentionCleanup,
            PayloadJson = JsonSerializer.Serialize(new { requestedAt = now }),
            Status = OutboxStatus.Pending,
            MaxAttempts = 3,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
