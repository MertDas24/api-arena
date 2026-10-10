using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace APIArena.Infrastructure.Web;

public static class HealthEndpoints
{
    /// <summary>
    /// /health/live: process is up (no dependency checks, so a DB outage does not restart healthy pods).
    /// /health/ready: dependencies (DB) reachable; returns 503 when not.
    /// </summary>
    public static IEndpointRouteBuilder MapApiArenaHealth(this IEndpointRouteBuilder app, string plane)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(DependencyInjection.ReadyTag),
            ResponseWriter = (ctx, report) => ctx.Response.WriteAsJsonAsync(ToDto(report, plane, ctx)),
        }).AllowAnonymous();
        return app;
    }

    public static object ToDto(HealthReport report, string plane, HttpContext context) => new
    {
        status = report.Status.ToString(),
        plane,
        version = typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev",
        checkedAt = context.RequestServices.GetService(typeof(TimeProvider)) is TimeProvider tp ? tp.GetUtcNow() : DateTimeOffset.UtcNow,
        checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), durationMs = (int)e.Value.Duration.TotalMilliseconds }),
    };
}
