using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace APIArena.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` (migrations add / bundle). Reads APIARENA_DESIGN_CONNECTION or falls back to the local dev instance.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("APIARENA_DESIGN_CONNECTION")
            ?? "Server=localhost;Database=ApiArena_Dev;Integrated Security=true;Encrypt=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options);
    }
}
