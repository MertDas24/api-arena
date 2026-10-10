using System.Security.Cryptography;
using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Application.Options;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Runs;

public sealed record CreateRunRequest(string? Scenario, int? Version, string? Label, Guid? WebhookTargetId = null);

public sealed record RunKeyDto(Guid Id, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, bool Active);

public sealed record CreatedRunKeyDto(Guid Id, string Key, string Prefix, DateTimeOffset ExpiresAt, string SimulatorBaseUrl, int RevokedPrevious);

public sealed record RunDto(
    Guid Id,
    Guid ProjectId,
    string ProjectName,
    string ScenarioSlug,
    string ScenarioTitle,
    int ScenarioVersion,
    string Engine,
    string EvaluatorVersion,
    RunStatus Status,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset? ExpiresAt,
    int InboundCount,
    int InboundLimit,
    int TtlSeconds,
    bool QuotaExceeded,
    string? IncompleteReason,
    string SimulatorBaseUrl,
    string? Method,
    string? Route,
    string? SimulatorUrl,
    IReadOnlyList<RunKeyDto> Keys,
    bool HasReport,
    long LastSequence,
    DateTimeOffset ServerTime,
    JsonElement Config,
    Guid? WebhookTargetId);

public sealed record RunListItem(Guid Id, string ScenarioSlug, int ScenarioVersion, RunStatus Status, string? Label, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, int InboundCount, bool HasReport, int? Passed, int? Failed, int? Inconclusive);

/// <summary>Produces the immutable report for a run that reached a terminal state (implemented in stage 07).</summary>
public interface IRunFinalizer
{
    Task FinalizeAsync(Guid runId, CancellationToken ct);
}

public interface ISimulatorUrls
{
    string RunBaseUrl(Guid runId);
}

public sealed class RunEventLog(IAppDbContext db, IRunAtomics atomics, TimeProvider clock)
{
    /// <summary>Appends a lifecycle event with the next run sequence. Sequences are unique and increasing; gaps are allowed.</summary>
    public async Task<long> AppendAsync(Guid runId, string type, object? data, CancellationToken ct, Guid? observationId = null)
    {
        var seq = await atomics.AllocateSequenceAsync(runId, ct);
        db.RunEvents.Add(new RunEvent
        {
            RunId = runId,
            Sequence = seq,
            Type = type,
            OccurredAt = clock.GetUtcNow(),
            ObservationId = observationId,
            DataJson = data is null ? null : JsonSerializer.Serialize(data, ScenarioJson.Options),
        });
        await db.SaveChangesAsync(ct);
        return seq;
    }
}

public sealed class RunService(
    IAppDbContext db,
    AccessScope scope,
    IRunAtomics atomics,
    RunEventLog events,
    IRunFinalizer finalizer,
    IUserDirectory users,
    ISimulatorUrls simUrls,
    TimeProvider clock,
    IOptions<LimitOptions> limits,
    IOptions<AuthOptions> auth,
    IOptions<WebhookOptions> webhooks,
    IEnumerable<IRunLifecycleParticipant> participants)
{
    public static readonly string[] SortKeys = ["createdAt"];

    public async Task<RunDto> CreateAsync(Guid userId, Guid projectId, CreateRunRequest request, CancellationToken ct)
    {
        var project = await scope.RequireProjectAsync(userId, projectId, ct);
        if (string.IsNullOrWhiteSpace(request.Scenario))
        {
            throw ValidationFailedException.For("scenario", "Bir senaryo seçin.");
        }

        var label = request.Label?.Trim();
        if (label is { Length: > 80 } || (label?.Any(char.IsControl) ?? false))
        {
            throw ValidationFailedException.For("label", "Etiket en fazla 80 karakter olabilir.");
        }

        var template = await scope.RequireTemplateAsync(userId, request.Scenario.Trim(), ct);
        var version = request.Version is { } v
            ? template.Versions.FirstOrDefault(x => x.Version == v) ?? throw new NotFoundException("Senaryo sürümü")
            : template.Versions.OrderByDescending(x => x.Version).FirstOrDefault() ?? throw new NotFoundException("Senaryo sürümü");

        Guid? targetId = null;
        if (Engines.IsWebhook(version.Engine))
        {
            if (!webhooks.Value.Enabled)
            {
                throw new NotFoundException("Senaryo");
            }

            var now0 = clock.GetUtcNow();
            var target = request.WebhookTargetId is { } tid
                ? await db.WebhookTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid && t.ProjectId == project.Id && t.RevokedAt == null, ct)
                : null;
            if (target is null)
            {
                throw ValidationFailedException.For("webhookTargetId", "Bu proje için doğrulanmış bir webhook hedefi seçin.");
            }

            if (target.Status != WebhookTargetStatus.Verified || target.VerificationExpiresAt <= now0)
            {
                throw new ConflictException("target_not_verified", "Webhook hedefi doğrulanmamış veya doğrulamanın süresi dolmuş. Önce hedefi doğrulayın.");
            }

            targetId = target.Id;
        }
        else if (request.WebhookTargetId is not null)
        {
            throw ValidationFailedException.For("webhookTargetId", "HTTP senaryoları webhook hedefi kullanmaz.");
        }

        var l = limits.Value;
        var now = clock.GetUtcNow();
        var run = new Run
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ScenarioVersionId = version.Id,
            ScenarioSlug = template.Slug,
            ScenarioVersionNumber = version.Version,
            Engine = version.Engine,
            EvaluatorVersion = version.EvaluatorVersion,
            Seed = RandomNumberGenerator.GetInt32(int.MaxValue),
            InboundLimit = l.RequestsPerRun,
            TtlSeconds = l.RunTtlMinutes * 60,
            ConfigSnapshotJson = JsonSerializer.Serialize(new
            {
                definition = JsonDocument.Parse(version.DefinitionJson).RootElement,
                contentHash = version.ContentHash,
                captureBodies = project.CaptureBodies,
                limits = new { inbound = l.RequestsPerRun, ttlSeconds = l.RunTtlMinutes * 60, maxDelayMs = l.MaxDelayMs, maxBodyBytes = l.MaxRequestBodyBytes },
            }, ScenarioJson.Options),
            Label = string.IsNullOrEmpty(label) ? null : label,
            Status = RunStatus.Created,
            CreatedAt = now,
            WebhookTargetId = targetId,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await events.AppendAsync(run.Id, "RunCreated", new { scenario = run.ScenarioSlug, version = run.ScenarioVersionNumber }, ct);
        return await GetAsync(userId, run.Id, ct);
    }

    public async Task<PagedResult<RunListItem>> ListAsync(Guid userId, Guid projectId, PageRequest page, CancellationToken ct)
    {
        await scope.RequireProjectAsync(userId, projectId, ct);
        await ExpireDueAsync(ct, projectId: projectId);
        var query = scope.Runs(userId).Where(r => r.ProjectId == projectId);
        var total = await query.CountAsync(ct);
        query = page.Descending ? query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id) : query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id);
        var items = await query.Skip(page.Skip).Take(page.PageSize)
            .Select(r => new
            {
                Run = r,
                Report = db.ReportSnapshots.Where(s => s.RunId == r.Id).Select(s => new { s.Passed, s.Failed, s.Inconclusive }).FirstOrDefault(),
            })
            .AsNoTracking()
            .ToListAsync(ct);
        return new PagedResult<RunListItem>(items.Select(x => new RunListItem(
            x.Run.Id, x.Run.ScenarioSlug, x.Run.ScenarioVersionNumber, x.Run.Status, x.Run.Label, x.Run.CreatedAt, x.Run.StartedAt, x.Run.EndedAt,
            x.Run.InboundCount, x.Report is not null, x.Report?.Passed, x.Report?.Failed, x.Report?.Inconclusive)).ToList(), page.Page, page.PageSize, total);
    }

    public async Task<RunDto> GetAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        if (run.Status == RunStatus.Running && run.ExpiresAt <= clock.GetUtcNow())
        {
            await TransitionAsync(run.Id, [RunStatus.Running], RunStatus.Expired, IncompleteReasons.Expired, ct);
        }

        var data = await scope.Runs(userId).Where(r => r.Id == runId).AsNoTracking()
            .Select(r => new
            {
                Run = r,
                ProjectName = db.Projects.Where(p => p.Id == r.ProjectId).Select(p => p.Name).First(),
                Title = db.ScenarioVersions.Where(v => v.Id == r.ScenarioVersionId)
                    .Select(v => db.ScenarioTemplates.Where(t => t.Id == v.TemplateId).Select(t => t.Title).First()).First(),
                HasReport = db.ReportSnapshots.Any(s => s.RunId == r.Id),
                Keys = db.RunAccessKeys.Where(k => k.RunId == r.Id).OrderByDescending(k => k.CreatedAt).ToList(),
            })
            .FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Koşu");

        var now = clock.GetUtcNow();
        var r = data.Run;
        using var config = JsonDocument.Parse(r.ConfigSnapshotJson);
        var definition = config.RootElement.GetProperty("definition");
        var method = definition.TryGetProperty("method", out var m) ? m.GetString() : null;
        var route = definition.TryGetProperty("route", out var rt) ? rt.GetString() : null;
        var baseUrl = simUrls.RunBaseUrl(r.Id);
        return new RunDto(r.Id, r.ProjectId, data.ProjectName, r.ScenarioSlug, data.Title, r.ScenarioVersionNumber, r.Engine, r.EvaluatorVersion, r.Status, r.Label,
            r.CreatedAt, r.StartedAt, r.EndedAt, r.ExpiresAt, r.InboundCount, r.InboundLimit, r.TtlSeconds, r.QuotaExceeded, r.IncompleteReason,
            baseUrl, method, route, route is null ? null : $"{baseUrl}/{route}",
            data.Keys.Select(k => new RunKeyDto(k.Id, k.DisplayPrefix, k.CreatedAt, k.ExpiresAt, k.RevokedAt, k.RevokedAt is null && k.ExpiresAt > now)).ToList(),
            data.HasReport, r.NextSequence, now, config.RootElement.Clone(), r.WebhookTargetId);
    }

    /// <summary>
    /// Created → Running. Idempotent under concurrency: the per-user lock serialises starts (so the active-run and
    /// daily quotas cannot be exceeded by parallel calls) and the conditional UPDATE guarantees a single transition.
    /// </summary>
    public async Task<RunDto> StartAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        var user = await users.FindAsync(userId, ct) ?? throw new NotFoundException("Kullanıcı");
        if (auth.Value.RequireVerifiedEmailForRuns && !user.EmailConfirmed)
        {
            throw new ForbiddenException("email_not_verified", "Koşu başlatmak için önce e-posta adresinizi doğrulayın.");
        }

        await atomics.WithUserLockAsync(userId, async () =>
        {
            var run = await scope.Runs(userId).AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("Koşu");
            if (run.Status == RunStatus.Running)
            {
                return true; // idempotent
            }

            if (run.Status != RunStatus.Created)
            {
                throw new ConflictException("invalid_run_state", $"Koşu '{run.Status}' durumunda; yalnızca yeni (Created) koşu başlatılabilir.");
            }

            var now = clock.GetUtcNow();
            var l = limits.Value;
            // Deleted projects are included so deleting a project cannot be used to reset quotas.
            var userRuns = db.Runs.Where(r => db.Projects.Any(p => p.Id == r.ProjectId && p.OwnerId == userId));
            var active = await userRuns.CountAsync(r => r.Status == RunStatus.Running && r.ExpiresAt > now, ct);
            if (active >= l.ActiveRunsPerUser)
            {
                throw new QuotaExceededException("quota_active_runs", $"Aynı anda en fazla {l.ActiveRunsPerUser} aktif koşu olabilir. Önce bir koşuyu bitirin veya iptal edin.");
            }

            var since = now.AddHours(-24);
            var daily = await userRuns.CountAsync(r => r.StartedAt != null && r.StartedAt > since, ct);
            if (daily >= l.DailyRunsPerUser)
            {
                throw new QuotaExceededException("quota_daily_runs", $"Son 24 saatte en fazla {l.DailyRunsPerUser} koşu başlatılabilir.");
            }

            var expires = now.AddSeconds(run.TtlSeconds);
            var updated = await db.Runs.Where(r => r.Id == runId && r.Status == RunStatus.Created)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, RunStatus.Running)
                    .SetProperty(r => r.StartedAt, now)
                    .SetProperty(r => r.ExpiresAt, expires), ct);
            if (updated == 1)
            {
                await events.AppendAsync(runId, "RunStarted", new { expiresAt = expires }, ct);
                foreach (var p in participants)
                {
                    await p.OnStartedAsync(runId, ct);
                }
            }

            return true;
        }, ct);
        return await GetAsync(userId, runId, ct);
    }

    public async Task<RunDto> FinishAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        var target = run.Status == RunStatus.Running && run.ExpiresAt <= clock.GetUtcNow() ? RunStatus.Expired : RunStatus.Completed;
        if (run.Status == RunStatus.Created)
        {
            throw new ConflictException("invalid_run_state", "Başlatılmamış koşu bitirilemez; iptal edebilirsiniz.");
        }

        await EndAsync(run.Id, [RunStatus.Running], target, target == RunStatus.Expired ? IncompleteReasons.Expired : null, ct);
        return await GetAsync(userId, runId, ct);
    }

    public async Task<RunDto> CancelAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        await EndAsync(run.Id, [RunStatus.Created, RunStatus.Running], RunStatus.Cancelled, IncompleteReasons.Cancelled, ct);
        return await GetAsync(userId, runId, ct);
    }

    private async Task EndAsync(Guid runId, RunStatus[] from, RunStatus target, string? reason, CancellationToken ct)
    {
        if (!await TransitionAsync(runId, from, target, reason, ct))
        {
            var current = await db.Runs.Where(r => r.Id == runId).Select(r => r.Status).FirstAsync(ct);
            if (current != target)
            {
                // Lost a finish/cancel race (or invalid state): the run keeps the single final state that won.
                throw new ConflictException("run_already_ended", $"Koşu zaten '{current}' durumunda sonlandı.");
            }
        }
    }

    /// <summary>Conditional single-row transition; returns false if another caller already moved the run.</summary>
    public async Task<bool> TransitionAsync(Guid runId, RunStatus[] from, RunStatus to, string? reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var updated = await db.Runs.Where(r => r.Id == runId && from.Contains(r.Status))
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, to)
                .SetProperty(r => r.EndedAt, now)
                .SetProperty(r => r.IncompleteReason, r => reason ?? r.IncompleteReason), ct);
        if (updated == 0)
        {
            return false;
        }

        await db.RunAccessKeys.Where(k => k.RunId == runId && k.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, now), ct);
        await events.AppendAsync(runId, "Run" + to, reason is null ? null : new { reason }, ct);
        foreach (var p in participants)
        {
            await p.OnEndedAsync(runId, ct);
        }

        await finalizer.FinalizeAsync(runId, ct);
        return true;
    }

    /// <summary>Expires overdue runs (worker sweep + lazy on read). Safe to run concurrently and after restarts.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct, int batch = 50, Guid? projectId = null)
    {
        var now = clock.GetUtcNow();
        var query = db.Runs.Where(r => r.Status == RunStatus.Running && r.ExpiresAt <= now);
        if (projectId is { } p)
        {
            query = query.Where(r => r.ProjectId == p);
        }

        var due = await query.OrderBy(r => r.ExpiresAt).Select(r => r.Id).Take(batch).ToListAsync(ct);
        var count = 0;
        foreach (var id in due)
        {
            if (await TransitionAsync(id, [RunStatus.Running], RunStatus.Expired, IncompleteReasons.Expired, ct))
            {
                count++;
            }
        }

        return count;
    }

    public async Task<CreatedRunKeyDto> CreateKeyAsync(Guid userId, Guid runId, CancellationToken ct)
    {
        var run = await scope.RequireRunAsync(userId, runId, ct);
        if (RunStateMachine.IsTerminal(run.Status))
        {
            throw new ConflictException("run_ended", "Bitmiş koşu için anahtar oluşturulamaz. Yeni bir koşu başlatın.");
        }

        var l = limits.Value;
        if (await db.RunAccessKeys.CountAsync(k => k.RunId == runId, ct) >= l.MaxKeysPerRun)
        {
            throw new QuotaExceededException("quota_run_keys", $"Bir koşu için en fazla {l.MaxKeysPerRun} anahtar oluşturulabilir.");
        }

        var now = clock.GetUtcNow();
        // Rotation: issuing a new key revokes all previous ones for this run.
        var revoked = await db.RunAccessKeys.Where(k => k.RunId == runId && k.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, now), ct);

        var plaintext = RunKeys.Generate();
        var entity = new RunAccessKey
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            KeyHash = RunKeys.Hash(plaintext),
            DisplayPrefix = RunKeys.DisplayPrefix(plaintext),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(l.RunKeyTtlMinutes),
        };
        db.RunAccessKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        await events.AppendAsync(runId, "KeyIssued", new { prefix = entity.DisplayPrefix, revokedPrevious = revoked }, ct);
        return new CreatedRunKeyDto(entity.Id, plaintext, entity.DisplayPrefix, entity.ExpiresAt, simUrls.RunBaseUrl(runId), revoked);
    }

    public async Task RevokeKeyAsync(Guid userId, Guid runId, Guid keyId, CancellationToken ct)
    {
        await scope.RequireRunAsync(userId, runId, ct);
        var now = clock.GetUtcNow();
        var updated = await db.RunAccessKeys.Where(k => k.Id == keyId && k.RunId == runId && k.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, now), ct);
        if (updated == 0 && !await db.RunAccessKeys.AnyAsync(k => k.Id == keyId && k.RunId == runId, ct))
        {
            throw new NotFoundException("Anahtar");
        }

        if (updated == 1)
        {
            await events.AppendAsync(runId, "KeyRevoked", new { keyId }, ct);
        }
    }
}
