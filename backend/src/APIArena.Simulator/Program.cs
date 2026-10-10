using APIArena.Infrastructure;
using APIArena.Application.Options;
using APIArena.Infrastructure.Web;
using APIArena.Simulator;
using APIArena.Infrastructure.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDeploymentFile();

var limits = builder.Configuration.GetSection(LimitOptions.Section).Get<LimitOptions>() ?? new LimitOptions();
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
    k.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeadersBytes;
    // Request line = method + target + version; the URL itself is additionally checked against MaxUrlLength.
    k.Limits.MaxRequestLineSize = limits.MaxUrlLength + 64;
});

builder.Services.AddApiArenaCore(builder.Configuration, builder.Environment);
builder.Services.AddApiArenaProblemDetails("simulator");
builder.Services.AddSimulator();

var app = builder.Build();

app.UseExceptionHandler();
app.Use(CorrelationId.Middleware);
app.Use(SecurityHeaders.ApiMiddleware);
app.UseStatusCodePages();
app.MapApiArenaHealth("simulator");
app.MapSimulator();

app.Run();

namespace APIArena.Simulator
{
    /// <summary>Entry-assembly marker for WebApplicationFactory.</summary>
    public sealed class SimulatorMarker;
}
