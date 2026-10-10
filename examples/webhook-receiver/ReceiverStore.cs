using System.Data;
using Microsoft.Data.SqlClient;

namespace ApiArena.WebhookReceiver;

public sealed record PaymentEvent(string Id, string Type, Guid RunId, string AggregateId, int Version, PaymentData Data, bool Synthetic);

public sealed record PaymentData(string PaymentId, string Status, long Amount, string Currency);

public enum ApplyResult
{
    Applied,
    DuplicateEventIgnored,
    StaleEventIgnored,
}

/// <summary>
/// Persistent store for the sample payment receiver. The two guarantees the API Arena v1 scenarios check:
/// 1) Idempotency: ProcessedEvents.EventId is a PRIMARY KEY inserted in the SAME transaction as the business update,
///    so two parallel deliveries of one event produce exactly one update (the loser hits the key and becomes a no-op).
/// 2) Ordering: the business update only applies if the event version is newer (Version &lt; @version guard), so a
///    stale event arriving late cannot overwrite newer state.
/// All amounts are synthetic; no real payment is processed.
/// </summary>
public sealed class ReceiverStore(string connectionString, bool naive = false)
{
    public bool Naive { get; } = naive;

    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            IF OBJECT_ID('dbo.Payments') IS NULL
                CREATE TABLE dbo.Payments (AggregateId nvarchar(80) NOT NULL PRIMARY KEY, Status nvarchar(40) NOT NULL,
                    Version int NOT NULL, Amount bigint NOT NULL, Currency nvarchar(3) NOT NULL, RunId uniqueidentifier NOT NULL,
                    UpdateCount int NOT NULL DEFAULT 0);
            IF OBJECT_ID('dbo.ProcessedEvents') IS NULL
                CREATE TABLE dbo.ProcessedEvents (EventId nvarchar(80) NOT NULL PRIMARY KEY, RunId uniqueidentifier NOT NULL,
                    Applied bit NOT NULL, ProcessedAt datetimeoffset NOT NULL);
            IF OBJECT_ID('dbo.SeenDeliveries') IS NULL
                CREATE TABLE dbo.SeenDeliveries (DeliveryId nvarchar(80) NOT NULL PRIMARY KEY, SeenAt datetimeoffset NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replay protection: a delivery id is accepted once (a resent delivery after a sender crash is acknowledged, not re-applied).</summary>
    public async Task<bool> TryRecordDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand("INSERT INTO dbo.SeenDeliveries (DeliveryId, SeenAt) VALUES (@d, SYSUTCDATETIME())", c);
        cmd.Parameters.AddWithValue("@d", deliveryId);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            return false;
        }
    }

    public async Task<ApplyResult> ApplyAsync(PaymentEvent e, CancellationToken ct)
    {
        if (Naive)
        {
            return await ApplyNaiveAsync(e, ct);
        }

        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // 1) Claim the event id first: a concurrent duplicate blocks here and then fails on the primary key.
            await using (var claim = new SqlCommand("INSERT INTO dbo.ProcessedEvents (EventId, RunId, Applied, ProcessedAt) VALUES (@e, @r, 0, SYSUTCDATETIME())", c, tx))
            {
                claim.Parameters.AddWithValue("@e", e.Id);
                claim.Parameters.AddWithValue("@r", e.RunId);
                await claim.ExecuteNonQueryAsync(ct);
            }

            // 2) Version-guarded upsert of the business row.
            await using var upd = new SqlCommand("""
                UPDATE dbo.Payments SET Status = @s, Version = @v, Amount = @a, Currency = @c, UpdateCount = UpdateCount + 1
                WHERE AggregateId = @id AND Version < @v;
                IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM dbo.Payments WITH (UPDLOCK, HOLDLOCK) WHERE AggregateId = @id)
                BEGIN
                    INSERT INTO dbo.Payments (AggregateId, Status, Version, Amount, Currency, RunId, UpdateCount) VALUES (@id, @s, @v, @a, @c, @r, 1);
                    SELECT 1;
                END
                ELSE SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Payments WHERE AggregateId = @id AND Version = @v AND Status = @s) THEN 1 ELSE 0 END;
                """, c, tx);
            upd.Parameters.AddWithValue("@id", e.AggregateId);
            upd.Parameters.AddWithValue("@s", e.Data.Status);
            upd.Parameters.AddWithValue("@v", e.Version);
            upd.Parameters.AddWithValue("@a", e.Data.Amount);
            upd.Parameters.AddWithValue("@c", e.Data.Currency);
            upd.Parameters.AddWithValue("@r", e.RunId);
            var applied = Convert.ToInt32(await upd.ExecuteScalarAsync(ct)) == 1;

            await using (var mark = new SqlCommand("UPDATE dbo.ProcessedEvents SET Applied = @ap WHERE EventId = @e", c, tx))
            {
                mark.Parameters.AddWithValue("@ap", applied);
                mark.Parameters.AddWithValue("@e", e.Id);
                await mark.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return applied ? ApplyResult.Applied : ApplyResult.StaleEventIgnored;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            await tx.RollbackAsync(CancellationToken.None);
            return ApplyResult.DuplicateEventIgnored;
        }
    }

    /// <summary>The common mistake, kept for the demo's "before" run: no dedup, last write wins.</summary>
    private async Task<ApplyResult> ApplyNaiveAsync(PaymentEvent e, CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            MERGE dbo.Payments AS t USING (SELECT @id AS AggregateId) AS s ON t.AggregateId = s.AggregateId
            WHEN MATCHED THEN UPDATE SET Status = @s, Version = @v, Amount = t.Amount + @a, UpdateCount = t.UpdateCount + 1
            WHEN NOT MATCHED THEN INSERT (AggregateId, Status, Version, Amount, Currency, RunId, UpdateCount) VALUES (@id, @s, @v, @a, @c, @r, 1);
            MERGE dbo.ProcessedEvents AS t USING (SELECT @e AS EventId) AS s ON t.EventId = s.EventId
            WHEN MATCHED THEN UPDATE SET Applied = 1
            WHEN NOT MATCHED THEN INSERT (EventId, RunId, Applied, ProcessedAt) VALUES (@e, @r, 1, SYSUTCDATETIME());
            """, c);
        cmd.Parameters.AddWithValue("@id", e.AggregateId);
        cmd.Parameters.AddWithValue("@s", e.Data.Status);
        cmd.Parameters.AddWithValue("@v", e.Version);
        cmd.Parameters.AddWithValue("@a", e.Data.Amount);
        cmd.Parameters.AddWithValue("@c", e.Data.Currency);
        cmd.Parameters.AddWithValue("@r", e.RunId);
        cmd.Parameters.AddWithValue("@e", e.Id);
        await cmd.ExecuteNonQueryAsync(ct);
        return ApplyResult.Applied;
    }

    public sealed record AggregateRow(string AggregateId, string Status, int Version, long Amount, int UpdateCount);

    public async Task<IReadOnlyList<AggregateRow>> AggregatesAsync(Guid runId, IEnumerable<string> ids, CancellationToken ct)
    {
        var list = new List<AggregateRow>();
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        foreach (var id in ids.Take(20))
        {
            await using var cmd = new SqlCommand("SELECT AggregateId, Status, Version, Amount, UpdateCount FROM dbo.Payments WHERE AggregateId = @id AND RunId = @r", c);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@r", runId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                list.Add(new AggregateRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4)));
            }
        }

        return list;
    }

    /// <summary>processedCount = how many times the event's business effect was applied (naive mode can exceed 1).</summary>
    public async Task<int> AppliedCountAsync(Guid runId, string eventId, string aggregateId, CancellationToken ct)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT CAST(Applied AS int) FROM dbo.ProcessedEvents WHERE EventId = @e AND RunId = @r", c);
        cmd.Parameters.AddWithValue("@e", eventId);
        cmd.Parameters.AddWithValue("@r", runId);
        var applied = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        if (!Naive || applied == 0)
        {
            return applied;
        }

        // In naive mode every delivery re-applies: count updates of the aggregate attributable to this event id.
        await using var count = new SqlCommand("SELECT UpdateCount FROM dbo.Payments WHERE AggregateId = @id AND RunId = @r", c);
        count.Parameters.AddWithValue("@id", aggregateId);
        count.Parameters.AddWithValue("@r", runId);
        return Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);
    }
}
