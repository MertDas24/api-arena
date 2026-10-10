using System.Security.Claims;
using System.Threading.RateLimiting;
using APIArena.Application.Reports;

namespace APIArena.Api.Endpoints;

public static class ShareEndpoints
{
    public const string PublicPolicy = "public-share";

    public static void AddShareRateLimits(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions o) =>
        o.AddPolicy(PublicPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public static void MapShareEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/runs/{runId:guid}/shares").WithTags("shares");
        g.MapGet("/", (ClaimsPrincipal user, ShareService svc, Guid runId, CancellationToken ct) => svc.ListAsync(user.Id(), runId, ct));
        g.MapPost("/", async (ClaimsPrincipal user, ShareService svc, Guid runId, CreateShareRequest body, HttpContext http, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(user.Id(), runId, body, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/runs/{runId}/shares/{created.Id}", created);
        });
        g.MapDelete("/{shareId:guid}", async (ClaimsPrincipal user, ShareService svc, Guid runId, Guid shareId, CancellationToken ct) =>
        {
            await svc.RevokeAsync(user.Id(), runId, shareId, ct);
            return Results.NoContent();
        });

        // Anonymous, read-only, sanitised. Not indexable; no referrer leaves the page (global Referrer-Policy: no-referrer).
        app.MapGet("/api/public/shares/{token}", async (ShareService svc, string token, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
            return Results.Ok(await svc.GetPublicAsync(token, ct));
        }).AllowAnonymous().RequireRateLimiting(PublicPolicy).WithTags("shares");
    }
}
