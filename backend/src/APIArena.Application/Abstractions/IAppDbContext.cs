using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace APIArena.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<Project> Projects { get; }
    DbSet<ScenarioTemplate> ScenarioTemplates { get; }
    DbSet<ScenarioVersion> ScenarioVersions { get; }
    DbSet<Run> Runs { get; }
    DbSet<RunOperation> RunOperations { get; }
    DbSet<RequestObservation> RequestObservations { get; }
    DbSet<RunEvent> RunEvents { get; }
    DbSet<RunAccessKey> RunAccessKeys { get; }
    DbSet<ReportSnapshot> ReportSnapshots { get; }
    DbSet<CheckResult> CheckResults { get; }
    DbSet<WebhookTarget> WebhookTargets { get; }
    DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts { get; }
    DbSet<OutboxJob> OutboxJobs { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<ShareLink> ShareLinks { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public sealed record InboundAllocation(long Sequence, int InboundCount, DateTimeOffset? PreviousRateLimitedUntil);

public enum InboundRejection
{
    None = 0,
    NotRunning = 1,
    Expired = 2,
    QuotaExceeded = 3,
}

/// <summary>
/// Atomic, multi-instance-safe counters implemented with single SQL statements (ADR-010).
/// All methods participate in the caller's ambient EF transaction when one is open.
/// </summary>
public interface IRunAtomics
{
    /// <summary>
    /// In one UPDATE: checks Running + not expired + quota, increments InboundCount and NextSequence and, when
    /// <paramref name="rateLimitUntilIfUnset"/> is given and the run has no window yet, opens the 429 window.
    /// </summary>
    Task<(InboundAllocation? Allocation, InboundRejection Rejection)> AllocateInboundAsync(Guid runId, DateTimeOffset now, DateTimeOffset? rateLimitUntilIfUnset, CancellationToken ct);

    /// <summary>Allocates the next event sequence regardless of run status (for outcome/lifecycle events).</summary>
    Task<long> AllocateSequenceAsync(Guid runId, CancellationToken ct);

    /// <summary>Upserts the per-operation attempt counter and returns the attempt number (1-based).</summary>
    Task<int> IncrementOperationAsync(Guid runId, string operationKey, DateTimeOffset now, CancellationToken ct);

    /// <summary>Runs <paramref name="body"/> inside a transaction holding an exclusive per-user application lock.</summary>
    Task<T> WithUserLockAsync<T>(Guid userId, Func<Task<T>> body, CancellationToken ct);
}
