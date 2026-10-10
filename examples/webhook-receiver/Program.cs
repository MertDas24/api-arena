using ApiArena.WebhookReceiver;

// Sample receiver for API Arena's duplicate/out-of-order webhook scenarios (local demo only).
//   RECEIVER_SIGNING_SECRET  secret shown once when you create the webhook target (never commit it)
//   RECEIVER_SQL             SQL Server connection string (default: local Windows auth, database ApiArenaReceiverDemo)
//   RECEIVER_MODE            "correct" (default) or "naive" (no dedup / no version guard — the "before" demo)
var builder = WebApplication.CreateBuilder(args);
var secret = Environment.GetEnvironmentVariable("RECEIVER_SIGNING_SECRET");
if (string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine("RECEIVER_SIGNING_SECRET ortam değişkenini ayarlayın (webhook hedefi oluşturulurken bir kez gösterilen sır).");
    return 2;
}

var sql = Environment.GetEnvironmentVariable("RECEIVER_SQL")
    ?? "Server=localhost;Database=ApiArenaReceiverDemo;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";
var naive = string.Equals(Environment.GetEnvironmentVariable("RECEIVER_MODE"), "naive", StringComparison.OrdinalIgnoreCase);

await EnsureDatabaseAsync(sql);
var store = new ReceiverStore(sql, naive);
await store.EnsureSchemaAsync(CancellationToken.None);

var app = builder.Build();
ReceiverEndpoints.Map(app, store, new ReceiverOptions { SigningSecret = secret });
Console.WriteLine($"Sample receiver ({(naive ? "NAIVE" : "correct")} mode) — POST /webhooks/apiarena, adapter POST /webhooks/apiarena/adapter");
app.Run();
return 0;

static async Task EnsureDatabaseAsync(string connectionString)
{
    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
    var database = builder.InitialCatalog;
    builder.InitialCatalog = "master";
    await using var c = new Microsoft.Data.SqlClient.SqlConnection(builder.ConnectionString);
    await c.OpenAsync();
    await using var cmd = c.CreateCommand();
    cmd.CommandText = "IF DB_ID(@db) IS NULL EXEC('CREATE DATABASE [' + @db + ']')";
    cmd.Parameters.AddWithValue("@db", database);
    await cmd.ExecuteNonQueryAsync();
}
