using APIArena.Application.Abstractions;
using APIArena.Application.Common;
using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Application.Access;

/// <summary>
/// The single place that decides what a user may see (OWN-01). Every project/run-scoped query starts here, so an
/// endpoint cannot "forget" an ownership check. Foreign or soft-deleted resources are reported as 404 (no existence leak).
/// Ownership always comes from the authenticated principal, never from request bodies.
/// </summary>
public sealed class AccessScope(IAppDbContext db)
{
    public IQueryable<Project> Projects(Guid userId) =>
        db.Projects.Where(p => p.OwnerId == userId && p.DeletedAt == null);

    public IQueryable<Run> Runs(Guid userId) =>
        db.Runs.Where(r => db.Projects.Any(p => p.Id == r.ProjectId && p.OwnerId == userId && p.DeletedAt == null));

    public IQueryable<ScenarioTemplate> VisibleTemplates(Guid userId) =>
        db.ScenarioTemplates.Where(t => !t.IsArchived && (t.OwnerId == null || t.OwnerId == userId));

    public async Task<Project> RequireProjectAsync(Guid userId, Guid projectId, CancellationToken ct) =>
        await Projects(userId).FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Proje");

    public async Task<Run> RequireRunAsync(Guid userId, Guid runId, CancellationToken ct) =>
        await Runs(userId).FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("Koşu");

    public async Task<ScenarioTemplate> RequireTemplateAsync(Guid userId, string slugOrId, CancellationToken ct)
    {
        var query = VisibleTemplates(userId).Include(t => t.Versions);
        var template = Guid.TryParse(slugOrId, out var id)
            ? await query.FirstOrDefaultAsync(t => t.Id == id, ct)
            : await query.FirstOrDefaultAsync(t => t.Slug == slugOrId && t.OwnerId == null, ct);
        return template ?? throw new NotFoundException("Senaryo");
    }
}
