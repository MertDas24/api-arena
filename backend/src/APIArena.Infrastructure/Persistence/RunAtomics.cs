using System.Data;
using APIArena.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementations of the atomic counters (ADR-010). Each operation is one parameterised statement whose
/// row lock is held only for the statement (or the caller's short transaction) — never across network waits.
/// </summary>
internal sealed class RunAtomics(AppDbContext db) : IRunAtomics
{
    private sealed record AllocationRow(long Sequence, int InboundCount, DateTimeOffset? PreviousRateLimitedUntil);

    private sealed record RunStateRow(int Status, DateTimeOffset? ExpiresAt, int InboundCount, int InboundLimit);

    public async Task<(InboundAllocation? Allocation, InboundRejection Rejection)> AllocateInboundAsync(
        Guid runId, DateTimeOffset now, DateTimeOffset? rateLimitUntilIfUnset, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<AllocationRow>($"""
            UPDATE [Runs] SET
                [NextSequence] = [NextSequence] + 1,
                [InboundCount] = [InboundCount] + 1,
                [RateLimitedUntil] = CASE WHEN {rateLimitUntilIfUnset} IS NOT NULL AND [RateLimitedUntil] IS NULL
                                          THEN {rateLimitUntilIfUnset} ELSE [RateLimitedUntil] END
            OUTPUT inserted.[NextSequence] AS [Sequence], inserted.[InboundCount] AS [InboundCount],
                   deleted.[RateLimitedUntil] AS [PreviousRateLimitedUntil]
            WHERE [Id] = {runId} AND [Status] = 1 AND [ExpiresAt] > {now} AND [InboundCount] < [InboundLimit]
            """).ToListAsync(ct);

        if (rows.Count == 1)
        {
            var r = rows[0];
            return (new InboundAllocation(r.Sequence, r.InboundCount, r.PreviousRateLimitedUntil), InboundRejection.None);
        }

        // Explain the rejection (read-only, no lock needed).
        var state = await db.Database.SqlQuery<RunStateRow>($"""
            SELECT [Status], [ExpiresAt], [InboundCount], [InboundLimit] FROM [Runs] WHERE [Id] = {runId}
            """).ToListAsync(ct);
        if (state.Count == 0 || state[0].Status != 1)
        {
            return (null, InboundRejection.NotRunning);
        }

        if (state[0].ExpiresAt is { } exp && exp <= now)
        {
            return (null, InboundRejection.Expired);
        }

        await db.Database.ExecuteSqlAsync($"UPDATE [Runs] SET [QuotaExceeded] = 1 WHERE [Id] = {runId} AND [QuotaExceeded] = 0", ct);
        return (null, InboundRejection.QuotaExceeded);
    }

    public async Task<long> AllocateSequenceAsync(Guid runId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<long>($"""
            UPDATE [Runs] SET [NextSequence] = [NextSequence] + 1 OUTPUT inserted.[NextSequence] AS [Value] WHERE [Id] = {runId}
            """).ToListAsync(ct);
        return rows.Count == 1 ? rows[0] : throw new InvalidOperationException("Run not found while allocating a sequence.");
    }

    public async Task<int> IncrementOperationAsync(Guid runId, string operationKey, DateTimeOffset now, CancellationToken ct)
    {
        // MERGE WITH (HOLDLOCK) is the race-free upsert on SQL Server: concurrent first attempts serialise on the key range.
        var rows = await db.Database.SqlQuery<int>($"""
            MERGE [RunOperations] WITH (HOLDLOCK) AS t
            USING (SELECT {runId} AS [RunId], {operationKey} AS [OperationKey]) AS s
               ON t.[RunId] = s.[RunId] AND t.[OperationKey] = s.[OperationKey]
            WHEN MATCHED THEN UPDATE SET t.[Attempts] = t.[Attempts] + 1, t.[LastSeenAt] = {now}
            WHEN NOT MATCHED THEN INSERT ([RunId], [OperationKey], [Attempts], [FirstSeenAt], [LastSeenAt])
                 VALUES (s.[RunId], s.[OperationKey], 1, {now}, {now})
            OUTPUT inserted.[Attempts] AS [Value];
            """).ToListAsync(ct);
        return rows[0];
    }

    public async Task<T> WithUserLockAsync<T>(Guid userId, Func<Task<T>> body, CancellationToken ct)
    {
        var owned = db.Database.CurrentTransaction is null;
        var tx = owned ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        try
        {
            var resource = "apiarena:user:" + userId.ToString("N");
            var result = await db.Database.SqlQuery<int>($"""
                DECLARE @r int;
                EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
                SELECT @r AS [Value];
                """).ToListAsync(ct);
            if (result[0] < 0)
            {
                throw new TimeoutException("Could not acquire user lock.");
            }

            var value = await body();
            if (tx is not null)
            {
                await tx.CommitAsync(ct);
            }

            return value;
        }
        catch
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
    }
}
