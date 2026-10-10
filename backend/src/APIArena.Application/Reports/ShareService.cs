using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Application.Options;
using APIArena.Application.Projects;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Reports;

public sealed record CreateShareRequest(int? ExpiresInDays);

public sealed record ShareDto(Guid Id, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, bool Active, int ViewCount);

public sealed record CreatedShareDto(Guid Id, string Url, DateTimeOffset ExpiresAt);

public sealed record PublicTimelineEntry(long Sequence, long OffsetMs, string Method, string Route, int Attempt, int PlannedStatus, DeliveryOutcome Outcome, int? DurationMs);

public sealed record PublicDeliveryEntry(int EventVersion, int PlannedOrder, int AttemptNumber, DateTimeOffset? SentAt, int? ResponseStatus, string Outcome);

/// <summary>What anonymous share viewers receive. No project name, label, ids, operation keys, event ids, headers, bodies or e-mail.</summary>
public sealed record PublicReport(
    string ScenarioSlug,
    int ScenarioVersion,
    string ScenarioTitle,
    string EvaluatorVersion,
    RunStatus RunStatus,
    long? RunDurationMs,
    string? IncompleteReasonText,
    DateTimeOffset GeneratedAt,
    ReportCounts Counts,
    int RequestCount,
    int RejectedCount,
    IReadOnlyList<ReportCheck> Checks,
    IReadOnlyList<string> Unverifiable,
    IReadOnlyList<PublicTimelineEntry> Timeline,
    IReadOnlyList<PublicDeliveryEntry> Deliveries,
    bool AdapterUsed,
    string Notice,
    DateTimeOffset ShareExpiresAt);

/// <summary>Single sanitisation policy for anything leaving the authenticated area (REP-07).</summary>
public static class ReportSanitizer
{
    public static PublicReport ForPublic(ReportDocument r, DateTimeOffset expiresAt) => new(
        r.ScenarioSlug, r.ScenarioVersion, r.ScenarioTitle, r.EvaluatorVersion, r.RunStatus, r.RunDurationMs, r.IncompleteReasonText, r.GeneratedAt, r.Counts,
        r.RequestCount, r.RejectedCount, r.Checks, r.Unverifiable,
        r.Timeline.Select(t => new PublicTimelineEntry(t.Sequence, t.OffsetMs, t.Method, Route(t.Route), t.Attempt, t.PlannedStatus, t.Outcome, t.DurationMs)).ToList(),
        r.Deliveries.Select(d => new PublicDeliveryEntry(d.EventVersion, d.PlannedOrder, d.AttemptNumber, d.SentAt, d.ResponseStatus, d.Outcome)).ToList(),
        r.AdapterUsed, r.Notice, expiresAt);

    // Rejected requests may carry arbitrary paths typed by the client; only show them truncated.
    private static string Route(string route) => route.Length > 60 ? route[..60] + "…" : route;
}

public sealed class ShareService(IAppDbContext db, AccessScope scope, ReportService reports, TimeProvider clock, IOptions<UrlOptions> urls)
{
    public const string TokenPrefix = "aa_sh_";
    public const int MaxActivePerRun = 5;
    public const int MaxDays = 30;

    public async Task<CreatedShareDto> CreateAsync(Guid userId, Guid runId, CreateShareRequest request, CancellationToken ct)
    {
        await reports.GetAsync(userId, runId, ct); // ownership + report must exist
        var days = request.ExpiresInDays ?? 7;
        if (days is < 1 or > MaxDays)
        {
            throw ValidationFailedException.For("expiresInDays", $"Geçerlilik 1–{MaxDays} gün olmalı.");
        }

        var now = clock.GetUtcNow();
        if (await db.ShareLinks.CountAsync(s => s.RunId == runId && s.RevokedAt == null && s.ExpiresAt > now, ct) >= MaxActivePerRun)
        {
            throw new QuotaExceededException("quota_shares", $"Bir koşu için en fazla {MaxActivePerRun} aktif paylaşım olabilir.");
        }

        var token = OpaqueTokens.Generate(TokenPrefix);
        var share = new ShareLink
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            TokenHash = OpaqueTokens.Hash(token),
            DisplayPrefix = token[..10],
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
        };
        db.ShareLinks.Add(share);
        await db.SaveChangesAsync(ct);
        return new CreatedShareDto(share.Id, $"{urls.Value.FrontendBaseUrl.TrimEnd('/')}/share/{token}", share.ExpiresAt);
    }

    public async Task<IReadOnlyList<ShareDto>> ListAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        await scope.RequireRunAsync(userId, runId, ct);
        var now = clock.GetUtcNow();
        return (await db.ShareLinks.AsNoTracking().Where(s => s.RunId == runId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct))
            .Select(s => new ShareDto(s.Id, s.DisplayPrefix, s.CreatedAt, s.ExpiresAt, s.RevokedAt, s.RevokedAt is null && s.ExpiresAt > now, s.ViewCount)).ToList();
    }

    public async Task RevokeAsync(Guid userId, Guid runId, Guid shareId, CancellationToken ct)
    {
        await scope.RequireRunAsync(userId, runId, ct);
        var updated = await db.ShareLinks.Where(s => s.Id == shareId && s.RunId == runId && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, clock.GetUtcNow()), ct);
        if (updated == 0 && !await db.ShareLinks.AnyAsync(s => s.Id == shareId && s.RunId == runId, ct))
        {
            throw new NotFoundException("Paylaşım");
        }
    }

    /// <summary>Anonymous read. Unknown, malformed, revoked, expired or deleted-project tokens all look identical (404).</summary>
    public async Task<PublicReport> GetPublicAsync(string token, CancellationToken ct)
    {
        if (token.Length != 49 || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            throw new NotFoundException("Paylaşım");
        }

        var hash = OpaqueTokens.Hash(token);
        var now = clock.GetUtcNow();
        var row = await db.ShareLinks.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
            .Join(db.Runs, s => s.RunId, r => r.Id, (s, r) => new { ShareId = s.Id, s.ExpiresAt, r.ProjectId, RunId = r.Id })
            .Where(x => db.Projects.Any(p => p.Id == x.ProjectId && p.DeletedAt == null))
            .Select(x => new { x.ShareId, x.ExpiresAt, Json = db.ReportSnapshots.Where(rs => rs.RunId == x.RunId).Select(rs => rs.ReportJson).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (row?.Json is null)
        {
            throw new NotFoundException("Paylaşım");
        }

        await db.ShareLinks.Where(s => s.Id == row.ShareId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ViewCount, s => s.ViewCount + 1), ct);
        return ReportSanitizer.ForPublic(JsonSerializer.Deserialize<ReportDocument>(row.Json, ScenarioJson.Options)!, row.ExpiresAt);
    }
}

/// <summary>Project deletion revokes all its share links immediately (same transaction).</summary>
public sealed class ShareDeletionParticipant(IAppDbContext db) : IProjectDeletionParticipant
{
    public Task OnProjectDeletedAsync(Guid projectId, DateTimeOffset now, CancellationToken ct) =>
        db.ShareLinks.Where(s => s.RevokedAt == null && db.Runs.Any(r => r.Id == s.RunId && r.ProjectId == projectId))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now), ct);
}
