using APIArena.Api;
using APIArena.Infrastructure;
using APIArena.Infrastructure.Web;
using APIArena.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using APIArena.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDeploymentFile();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 256 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

builder.Services.AddApiArenaCore(builder.Configuration, builder.Environment);
builder.Services.AddApiArenaProblemDetails("control");
builder.Services.AddControlPlane(builder.Configuration, builder.Environment);

var app = builder.Build();

// Explicit operator-controlled job, never run by normal application startup.
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<APIArena.Application.Scenarios.ScenarioSeeder>()
        .SeedAsync(APIArena.Domain.Scenarios.BuiltInScenarios.All, CancellationToken.None);
    return 0;
}

app.UseControlPlane();
app.MapApiArenaHealth("control");
app.MapControlPlane();

if (args.Contains("--seed-demo"))
{
    return await DemoSeed.RunAsync(app);
}

await app.SeedReferenceDataAsync();
app.Run();
return 0;

namespace APIArena.Api
{
    /// <summary>Entry-assembly marker for WebApplicationFactory.</summary>
    public sealed class ApiMarker;
}
