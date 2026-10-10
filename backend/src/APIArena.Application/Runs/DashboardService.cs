using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Options;
using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Runs;

public sealed record DashboardRun(Guid Id, Guid ProjectId, string ProjectName, string ScenarioSlug, int ScenarioVersion, RunStatus Status, string? Label,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool HasReport, int? Passed, int? Failed, int? Inconclusive);

public sealed record DashboardDto(
    int ProjectCount,
    int ActiveRuns,
    int ActiveRunLimit,
    int RunsStartedLast24h,
    int DailyRunLimit,
    int RequestsPerRun,
    int RunTtlMinutes,
    IReadOnlyList<DashboardRun> RecentRuns);

/// <summary>Only the caller's own data (via AccessScope); no global or demo statistics are mixed in.</summary>
public sealed class DashboardService(IAppDbContext db, AccessScope scope, TimeProvider clock, IOptions<LimitOptions> limits)
{
    public async Task<DashboardDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddHours(-24);
        var userRuns = db.Runs.Where(r => db.Projects.Any(p => p.Id == r.ProjectId && p.OwnerId == userId));
        var recent = await scope.Runs(userId).AsNoTracking().OrderByDescending(r => r.CreatedAt).Take(10)
            .Select(r => new
            {
                Run = r,
                Project = db.Projects.Where(p => p.Id == r.ProjectId).Select(p => p.Name).First(),
                Report = db.ReportSnapshots.Where(s => s.RunId == r.Id).Select(s => new { s.Passed, s.Failed, s.Inconclusive }).FirstOrDefault(),
            }).ToListAsync(ct);

        var l = limits.Value;
        return new DashboardDto(
            await scope.Projects(userId).CountAsync(ct),
            await userRuns.CountAsync(r => r.Status == RunStatus.Running && r.ExpiresAt > now, ct),
            l.ActiveRunsPerUser,
            await userRuns.CountAsync(r => r.StartedAt != null && r.StartedAt > since, ct),
            l.DailyRunsPerUser,
            l.RequestsPerRun,
            l.RunTtlMinutes,
            recent.Select(x => new DashboardRun(x.Run.Id, x.Run.ProjectId, x.Project, x.Run.ScenarioSlug, x.Run.ScenarioVersionNumber,
                x.Run.Status == RunStatus.Running && x.Run.ExpiresAt <= now ? RunStatus.Expired : x.Run.Status, x.Run.Label, x.Run.CreatedAt, x.Run.ExpiresAt,
                x.Report is not null, x.Report?.Passed, x.Report?.Failed, x.Report?.Inconclusive)).ToList());
    }
}
