using APIArena.Infrastructure.Persistence;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

[assembly: AssemblyFixture(typeof(SqlDatabaseFixture))]

namespace APIArena.IntegrationTests.Infrastructure;

/// <summary>
/// Creates a uniquely named database on the real SQL Server instance (APIARENA_TEST_SQL or local default),
/// applies all migrations once per test run and drops it afterwards. No in-memory/SQLite substitutes.
/// </summary>
public sealed class SqlDatabaseFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = $"ApiArena_Test_{Guid.NewGuid():N}";
    public string ConnectionString { get; }
    private readonly string _serverConnection;

    public SqlDatabaseFixture()
    {
        _serverConnection = Environment.GetEnvironmentVariable("APIARENA_TEST_SQL")
            ?? "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";
        var builder = new SqlConnectionStringBuilder(_serverConnection) { InitialCatalog = DatabaseName };
        ConnectionString = builder.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(_serverConnection);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}
