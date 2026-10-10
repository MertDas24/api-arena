using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Application.Options;
using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Projects;

// Request DTOs are allow-lists (OWN-03): there is no OwnerId, Id, DeletedAt or timestamp to mass-assign.
public sealed record CreateProjectRequest(string? Name, string? Description);

public sealed record UpdateProjectRequest(string? Name, string? Description, bool? CaptureBodies, string[]? AllowedOrigins);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string? Description,
    bool CaptureBodies,
    IReadOnlyList<string> AllowedOrigins,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int RunCount,
    int ActiveRunCount,
    DateTimeOffset? LastRunAt);

public sealed class ProjectService(IAppDbContext db, AccessScope scope, IRunAtomics atomics, TimeProvider clock, IOptions<LimitOptions> limits,
    IEnumerable<IProjectDeletionParticipant> deletionParticipants)
{
    public static readonly string[] SortKeys = ["createdAt", "name", "updatedAt"];

    public async Task<PagedResult<ProjectDto>> ListAsync(Guid userId, PageRequest page, string? search, CancellationToken ct)
    {
        var query = scope.Projects(userId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > 80)
            {
                throw ValidationFailedException.For("search", "Arama en fazla 80 karakter olabilir.");
            }

            query = query.Where(p => p.Name.Contains(term)); // parameterised by EF
        }

        var total = await query.CountAsync(ct);
        query = (page.Sort, page.Descending) switch
        {
            ("name", false) => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
            ("name", true) => query.OrderByDescending(p => p.Name).ThenBy(p => p.Id),
            ("updatedAt", false) => query.OrderBy(p => p.UpdatedAt).ThenBy(p => p.Id),
            ("updatedAt", true) => query.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id),
            (_, false) => query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
            _ => query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id),
        };
        var items = await Project(query.Skip(page.Skip).Take(page.PageSize)).ToListAsync(ct);
        return new PagedResult<ProjectDto>(items, page.Page, page.PageSize, total);
    }

    public async Task<ProjectDto> GetAsync(Guid userId, Guid projectId, CancellationToken ct) =>
        await Project(scope.Projects(userId).Where(p => p.Id == projectId)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Proje");

    public async Task<ProjectDto> CreateAsync(Guid userId, CreateProjectRequest request, CancellationToken ct)
    {
        var (name, description) = ValidateNameAndDescription(request.Name, request.Description, required: true);
        var count = await scope.Projects(userId).CountAsync(ct);
        if (count >= limits.Value.ProjectsPerUser)
        {
            throw new QuotaExceededException("quota_projects", $"En fazla {limits.Value.ProjectsPerUser} proje oluşturabilirsiniz.");
        }

        var now = clock.GetUtcNow();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            Name = name!,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, project.Id, ct);
    }

    public async Task<ProjectDto> UpdateAsync(Guid userId, Guid projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        var project = await scope.RequireProjectAsync(userId, projectId, ct);
        var (name, description) = ValidateNameAndDescription(request.Name, request.Description, required: false);
        if (name is not null)
        {
            project.Name = name;
        }

        if (request.Description is not null)
        {
            project.Description = description;
        }

        if (request.CaptureBodies is { } capture)
        {
            project.CaptureBodies = capture;
        }

        if (request.AllowedOrigins is not null)
        {
            project.SimulatorAllowedOrigins = string.Join('\n', OriginRules.Normalize(request.AllowedOrigins));
        }

        project.UpdatedAt = clock.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("concurrent_update", "Proje başka bir istekle değiştirildi. Sayfayı yenileyip tekrar deneyin.");
        }

        return await GetAsync(userId, projectId, ct);
    }

    /// <summary>
    /// Soft delete (ADR-011): hides the project immediately, cancels active runs and revokes their keys in one
    /// transaction (under the user lock so it cannot race a concurrent run start). Data is purged by retention.
    /// </summary>
    public async Task DeleteAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        await atomics.WithUserLockAsync(userId, async () =>
        {
            var project = await scope.RequireProjectAsync(userId, projectId, ct);
            var now = clock.GetUtcNow();
            project.DeletedAt = now;
            project.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            var runIds = db.Runs.Where(r => r.ProjectId == projectId).Select(r => r.Id);
            await db.Runs
                .Where(r => r.ProjectId == projectId && (r.Status == RunStatus.Created || r.Status == RunStatus.Running))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, RunStatus.Cancelled)
                    .SetProperty(r => r.EndedAt, now)
                    .SetProperty(r => r.IncompleteReason, "project_deleted"), ct);
            await db.RunAccessKeys
                .Where(k => runIds.Contains(k.RunId) && k.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, now), ct);
            foreach (var participant in deletionParticipants)
            {
                await participant.OnProjectDeletedAsync(projectId, now, ct);
            }

            return true;
        }, ct);
    }

    private IQueryable<ProjectDto> Project(IQueryable<Project> query) =>
        query.Select(p => new ProjectDto(
            p.Id,
            p.Name,
            p.Description,
            p.CaptureBodies,
            p.SimulatorAllowedOrigins == "" ? Array.Empty<string>() : p.SimulatorAllowedOrigins.Split('\n', StringSplitOptions.None),
            p.CreatedAt,
            p.UpdatedAt,
            db.Runs.Count(r => r.ProjectId == p.Id),
            db.Runs.Count(r => r.ProjectId == p.Id && (r.Status == RunStatus.Created || r.Status == RunStatus.Running)),
            db.Runs.Where(r => r.ProjectId == p.Id).Max(r => (DateTimeOffset?)r.CreatedAt)));

    private static (string? Name, string? Description) ValidateNameAndDescription(string? name, string? description, bool required)
    {
        var errors = new Dictionary<string, string[]>();
        var n = name?.Trim();
        if (required && string.IsNullOrEmpty(n))
        {
            errors["name"] = ["Proje adı zorunlu."];
        }
        else if (n is not null && (n.Length is < 1 or > 80 || n.Any(char.IsControl)))
        {
            errors["name"] = ["Proje adı 1–80 karakter olmalı ve kontrol karakteri içermemeli."];
        }

        var d = description?.Trim();
        if (d is { Length: > 500 })
        {
            errors["description"] = ["Açıklama en fazla 500 karakter olabilir."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        return (n, string.IsNullOrEmpty(d) ? null : d);
    }
}

/// <summary>Revokes dependent resources (share links, webhook jobs…) inside the project-deletion transaction.</summary>
public interface IProjectDeletionParticipant
{
    Task OnProjectDeletedAsync(Guid projectId, DateTimeOffset now, CancellationToken ct);
}

public static class OriginRules
{
    public const int MaxOrigins = 10;

    /// <summary>Exact scheme://host[:port] origins only; no wildcard, path, userinfo or query (SIM-09).</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> origins)
    {
        var result = new List<string>();
        foreach (var raw in origins)
        {
            var value = raw?.Trim() ?? "";
            if (value.Length == 0)
            {
                continue;
            }

            if (value.Contains('*') || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(uri.UserInfo) || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.Fragment)
                || value.TrimEnd('/').Length != uri.GetLeftPart(UriPartial.Authority).Length)
            {
                throw ValidationFailedException.For("allowedOrigins", $"Geçersiz origin: '{Truncate(value)}'. Örnek: https://app.example.com");
            }

            var origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            if (!result.Contains(origin))
            {
                result.Add(origin);
            }
        }

        if (result.Count > MaxOrigins)
        {
            throw ValidationFailedException.For("allowedOrigins", $"En fazla {MaxOrigins} origin eklenebilir.");
        }

        return result;
    }

    private static string Truncate(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
