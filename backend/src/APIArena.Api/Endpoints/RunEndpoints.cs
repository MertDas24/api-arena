using System.Security.Claims;
using APIArena.Application.Common;
using APIArena.Application.Reports;
using APIArena.Application.Runs;
using APIArena.Application.Scenarios;

namespace APIArena.Api.Endpoints;

public static class RunEndpoints
{
    /// <summary>Per-user polling budget for the live feed (bounded "connection" cost; 1.5 s polling uses ~40/min).</summary>
    public const string FeedPolicy = "feed";

    public static void MapScenarioAndRunEndpoints(this IEndpointRouteBuilder app)
    {
        var scenarios = app.MapGroup("/api/scenarios").WithTags("scenarios");
        scenarios.MapGet("/", (ClaimsPrincipal user, ScenarioCatalog catalog, CancellationToken ct) => catalog.ListAsync(user.Id(), ct));
        scenarios.MapGet("/{slugOrId}", (ClaimsPrincipal user, ScenarioCatalog catalog, string slugOrId, CancellationToken ct) =>
            catalog.GetAsync(user.Id(), slugOrId, ct));
        scenarios.MapGet("/{slugOrId}/versions/{version:int}", (ClaimsPrincipal user, ScenarioCatalog catalog, string slugOrId, int version, CancellationToken ct) =>
            catalog.GetVersionAsync(user.Id(), slugOrId, version, ct));

        app.MapGet("/api/projects/{projectId:guid}/runs", (ClaimsPrincipal user, RunService runs, Guid projectId, int? page, int? pageSize, string? order, CancellationToken ct) =>
            runs.ListAsync(user.Id(), projectId, PageRequest.Create(page, pageSize, "createdAt", order, RunService.SortKeys, "createdAt"), ct)).WithTags("runs");

        app.MapPost("/api/projects/{projectId:guid}/runs", async (ClaimsPrincipal user, RunService runs, Guid projectId, CreateRunRequest body, CancellationToken ct) =>
        {
            var run = await runs.CreateAsync(user.Id(), projectId, body, ct);
            return Results.Created($"/api/runs/{run.Id}", run);
        }).WithTags("runs");

        app.MapGet("/api/dashboard", (ClaimsPrincipal user, DashboardService dashboard, CancellationToken ct) => dashboard.GetAsync(user.Id(), ct)).WithTags("dashboard");

        app.MapGet("/api/runs/compare", (ClaimsPrincipal user, ReportService reports, Guid left, Guid right, CancellationToken ct) =>
            reports.CompareAsync(user.Id(), left, right, ct)).WithTags("reports");

        var g = app.MapGroup("/api/runs/{runId:guid}").WithTags("runs");
        g.MapGet("/report", (ClaimsPrincipal user, ReportService reports, Guid runId, CancellationToken ct) => reports.GetAsync(user.Id(), runId, ct));
        g.MapGet("/export", async (ClaimsPrincipal user, ReportService reports, Guid runId, string? format, CancellationToken ct) =>
        {
            var file = await reports.ExportAsync(user.Id(), runId, (format ?? "json").ToLowerInvariant(), ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        g.MapGet("/", (ClaimsPrincipal user, RunService runs, Guid runId, CancellationToken ct) => runs.GetAsync(user.Id(), runId, ct));
        g.MapPost("/start", (ClaimsPrincipal user, RunService runs, Guid runId, CancellationToken ct) => runs.StartAsync(user.Id(), runId, ct));
        g.MapPost("/finish", (ClaimsPrincipal user, RunService runs, Guid runId, CancellationToken ct) => runs.FinishAsync(user.Id(), runId, ct));
        g.MapPost("/cancel", (ClaimsPrincipal user, RunService runs, Guid runId, CancellationToken ct) => runs.CancelAsync(user.Id(), runId, ct));
        g.MapPost("/access-keys", async (ClaimsPrincipal user, RunService runs, Guid runId, HttpContext http, CancellationToken ct) =>
        {
            var key = await runs.CreateKeyAsync(user.Id(), runId, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/runs/{runId}/access-keys/{key.Id}", key);
        });
        g.MapGet("/events", (ClaimsPrincipal user, ObservationService obs, Guid runId, long? afterSequence, int? limit, CancellationToken ct) =>
            obs.FeedAsync(user.Id(), runId, afterSequence ?? 0, limit, ct)).RequireRateLimiting(FeedPolicy);
        g.MapGet("/observations", (ClaimsPrincipal user, ObservationService obs, Guid runId, int? page, int? pageSize, string? order, int? status,
            APIArena.Domain.Entities.DeliveryOutcome? outcome, string? operationKey, CancellationToken ct) =>
            obs.ListAsync(user.Id(), runId, PageRequest.Create(page, pageSize, "sequence", order ?? "asc", ObservationService.SortKeys, "sequence"), status, outcome, operationKey, ct));
        g.MapGet("/observations/{observationId:guid}", (ClaimsPrincipal user, ObservationService obs, Guid runId, Guid observationId, CancellationToken ct) =>
            obs.GetAsync(user.Id(), runId, observationId, ct));
        g.MapDelete("/access-keys/{keyId:guid}", async (ClaimsPrincipal user, RunService runs, Guid runId, Guid keyId, CancellationToken ct) =>
        {
            await runs.RevokeKeyAsync(user.Id(), runId, keyId, ct);
            return Results.NoContent();
        });
    }
}
