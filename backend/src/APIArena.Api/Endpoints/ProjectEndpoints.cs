using System.Security.Claims;
using APIArena.Application.Common;
using APIArena.Application.Projects;

namespace APIArena.Api.Endpoints;

public static class CurrentUser
{
    /// <summary>The only source of the acting user id: the authenticated principal (never a request field).</summary>
    public static Guid Id(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No user id claim."));
}

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").WithTags("projects");

        g.MapGet("/", (ClaimsPrincipal user, ProjectService projects, int? page, int? pageSize, string? sort, string? order, string? search, CancellationToken ct) =>
            projects.ListAsync(user.Id(), PageRequest.Create(page, pageSize, sort, order, ProjectService.SortKeys, "createdAt"), search, ct));

        g.MapPost("/", async (ClaimsPrincipal user, ProjectService projects, CreateProjectRequest body, CancellationToken ct) =>
        {
            var created = await projects.CreateAsync(user.Id(), body, ct);
            return Results.Created($"/api/projects/{created.Id}", created);
        });

        g.MapGet("/{id:guid}", (ClaimsPrincipal user, ProjectService projects, Guid id, CancellationToken ct) => projects.GetAsync(user.Id(), id, ct));

        g.MapPatch("/{id:guid}", (ClaimsPrincipal user, ProjectService projects, Guid id, UpdateProjectRequest body, CancellationToken ct) =>
            projects.UpdateAsync(user.Id(), id, body, ct));

        g.MapDelete("/{id:guid}", async (ClaimsPrincipal user, ProjectService projects, Guid id, CancellationToken ct) =>
        {
            await projects.DeleteAsync(user.Id(), id, ct);
            return Results.NoContent();
        });
    }
}
