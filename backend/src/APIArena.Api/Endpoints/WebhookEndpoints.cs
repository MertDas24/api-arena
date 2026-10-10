using System.Security.Claims;
using System.Threading.RateLimiting;
using APIArena.Application.Options;
using APIArena.Application.Webhooks;

namespace APIArena.Api.Endpoints;

/// <summary>
/// v1 webhook module. When ApiArena:Webhooks:Enabled is false (default) none of these routes exist (404) and the
/// worker registers no webhook handlers, so the feature is unreachable rather than merely hidden in the UI (WH-01).
/// </summary>
public static class WebhookEndpoints
{
    public const string VerifyPolicy = "webhook-verify";

    public static void AddWebhookRateLimits(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions o) =>
        o.AddPolicy(VerifyPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anon", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0,
            }));

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        var enabled = configuration.GetSection(WebhookOptions.Section).Get<WebhookOptions>()?.Enabled ?? false;
        app.MapGet("/api/features", (Microsoft.Extensions.Options.IOptions<APIArena.Application.Ops.RetentionOptions> retention) => Results.Ok(new
        {
            webhooks = enabled,
            retention = new { observationDays = retention.Value.ObservationDays, reportDays = retention.Value.ReportDays, deletedProjectDays = retention.Value.DeletedProjectDays },
        })).WithTags("features");
        if (!enabled)
        {
            return;
        }

        var g = app.MapGroup("/api/projects/{projectId:guid}/webhook-targets").WithTags("webhooks");
        g.MapGet("/", (ClaimsPrincipal user, WebhookTargetService svc, Guid projectId, CancellationToken ct) => svc.ListAsync(user.Id(), projectId, ct));
        g.MapPost("/", async (ClaimsPrincipal user, WebhookTargetService svc, Guid projectId, WebhookTargetRequest body, HttpContext http, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(user.Id(), projectId, body, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/projects/{projectId}/webhook-targets/{created.Target.Id}", created);
        });
        g.MapPatch("/{targetId:guid}", (ClaimsPrincipal user, WebhookTargetService svc, Guid projectId, Guid targetId, WebhookTargetRequest body, CancellationToken ct) =>
            svc.UpdateAsync(user.Id(), projectId, targetId, body, ct));
        g.MapPost("/{targetId:guid}/rotate-secret", (ClaimsPrincipal user, WebhookTargetService svc, Guid projectId, Guid targetId, CancellationToken ct) =>
            svc.RotateSecretAsync(user.Id(), projectId, targetId, ct));
        g.MapPost("/{targetId:guid}/verify", async (ClaimsPrincipal user, WebhookTargetService svc, Guid projectId, Guid targetId, CancellationToken ct) =>
            Results.Accepted(value: await svc.RequestVerificationAsync(user.Id(), projectId, targetId, ct))).RequireRateLimiting(VerifyPolicy);
        g.MapDelete("/{targetId:guid}", async (ClaimsPrincipal user, WebhookTargetService svc, Guid projectId, Guid targetId, CancellationToken ct) =>
        {
            await svc.DeleteAsync(user.Id(), projectId, targetId, ct);
            return Results.NoContent();
        });

        app.MapGet("/api/runs/{runId:guid}/deliveries", (ClaimsPrincipal user, DeliveryQueryService svc, Guid runId, CancellationToken ct) =>
            svc.ListAsync(user.Id(), runId, ct)).WithTags("webhooks");
    }
}
