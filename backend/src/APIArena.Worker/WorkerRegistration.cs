using APIArena.Application.Runs;
using APIArena.Infrastructure;

namespace APIArena.Worker;

public static class WorkerRegistration
{
    public static IServiceCollection AddWorkerJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHostedService<RunExpiryService>();
        services.AddApiArenaJobHandlers(configuration);
        services.AddHostedService<APIArena.Infrastructure.Jobs.OutboxWorker>();
        services.AddHostedService<APIArena.Infrastructure.Jobs.OpsScheduler>();
        return services;
    }
}

/// <summary>
/// Expires overdue runs and produces their reports. All state is in the database, so a restarted worker (or several
/// workers) simply continue: transitions are conditional single-row UPDATEs and finalisation is idempotent.
/// </summary>
public sealed class RunExpiryService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RunExpiryService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var expired = await scope.ServiceProvider.GetRequiredService<RunService>().ExpireDueAsync(stoppingToken);
                var heartbeat = Environment.GetEnvironmentVariable("APIARENA_WORKER_HEARTBEAT");
                if (!string.IsNullOrWhiteSpace(heartbeat))
                    await File.WriteAllTextAsync(heartbeat, "healthy", stoppingToken);
                if (expired > 0)
                {
                    logger.LogInformation("Expired {Count} overdue runs", expired);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Run expiry sweep failed ({Error}); retrying next interval", ex.GetType().Name);
            }

            await Task.Delay(Interval, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
