using System.Net;
using APIArena.Api.Endpoints;
using APIArena.Application.Options;
using APIArena.Infrastructure.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace APIArena.Api;

public static class ControlPlane
{
    public static IServiceCollection AddControlPlane(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var urls = configuration.GetSection(UrlOptions.Section).Get<UrlOptions>() ?? new UrlOptions();
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            // An empty allowlist means trust everyone in ForwardedHeadersMiddleware.
            // Keep loopback only for the native development proxy when no explicit proxies exist.
            if (urls.TrustedProxies.Length == 0)
            {
                o.KnownProxies.Add(IPAddress.Loopback);
                o.KnownProxies.Add(IPAddress.IPv6Loopback);
            }
            foreach (var proxy in urls.TrustedProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    o.KnownProxies.Add(ip);
                }
                else
                {
                    throw new InvalidOperationException("ApiArena:Urls:TrustedProxies must contain IP addresses.");
                }
            }
        });

        services.AddOpenApi();
        services.AddAuthPlane(configuration, environment);
        services.AddDomainEndpoints();
        return services;
    }

    public static WebApplication UseControlPlane(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.Use(CorrelationId.Middleware);
        app.Use(SecurityHeaders.ApiMiddleware);
        app.UseStatusCodePages();
        app.UseAuthPlane();
        return app;
    }

    /// <summary>
    /// Inserts built-in scenario versions (reference data, all environments). Failure (e.g. DB down) is logged and
    /// the host still starts; readiness reports the DB problem and the next start retries.
    /// </summary>
    public static async Task SeedReferenceDataAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<APIArena.Application.Scenarios.ScenarioSeeder>()
                .SeedAsync(APIArena.Domain.Scenarios.BuiltInScenarios.All, CancellationToken.None);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            app.Logger.LogError("Scenario seeding skipped: database unavailable ({Error}).", ex.GetType().Name);
        }
    }

    public static WebApplication MapControlPlane(this WebApplication app)
    {
        if (!app.Environment.IsProduction())
        {
            app.MapOpenApi("/api/openapi/{documentName}.json").AllowAnonymous();
        }

        // Summary used by the UI's status badge (proxied through Next.js as same-origin /api/health).
        app.MapGet("/api/health", async (HealthCheckService health, HttpContext ctx) =>
        {
            var report = await health.CheckHealthAsync(r => r.Tags.Contains(APIArena.Infrastructure.DependencyInjection.ReadyTag), ctx.RequestAborted);
            var dto = HealthEndpoints.ToDto(report, "control", ctx);
            return report.Status == HealthStatus.Healthy ? Results.Ok(dto) : Results.Json(dto, statusCode: 503);
        }).AllowAnonymous();

        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
        {
            app.MapDevMailbox();
        }

        app.MapAuthEndpoints();
        app.MapDomainEndpoints();
        return app;
    }
}
