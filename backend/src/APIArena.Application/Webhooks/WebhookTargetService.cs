using System.Text.Json;
using System.Text.RegularExpressions;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Application.Options;
using APIArena.Domain.Entities;
using APIArena.Domain.Outbound;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIArena.Application.Webhooks;

public static class JobTypes
{
    public const string VerifyTarget = "verify-target";
    public const string DeliverWebhook = "deliver-webhook";
    public const string FinalizeWebhookRun = "finalize-webhook-run";
    public const string RetentionCleanup = "retention-cleanup";
}

/// <summary>Validates outbound URLs with the environment-specific policy (dev-only localhost receiver exception).</summary>
public interface IWebhookUrlValidator
{
    (ValidatedTarget? Target, string? Error) Validate(string? url);
}

/// <summary>Encrypts/decrypts signing secrets (AES-GCM, key outside the DB).</summary>
public interface ISigningSecretStore
{
    (byte[] Ciphertext, string KeyId) Protect(string secret);

    string Reveal(byte[] ciphertext, string keyId);
}

public sealed record WebhookTargetDto(Guid Id, string Url, string? AdapterPath, WebhookTargetStatus Status, string SecretHint, DateTimeOffset? VerifiedAt,
    DateTimeOffset? VerificationExpiresAt, DateTimeOffset? LastVerificationAttemptAt, string? LastVerificationError, DateTimeOffset CreatedAt, bool IsDevLocal);

public sealed record CreatedWebhookTargetDto(WebhookTargetDto Target, string SigningSecret);

public sealed record WebhookTargetRequest(string? Url, string? AdapterPath);

public sealed class AuditLog(IAppDbContext db, TimeProvider clock)
{
    /// <summary>Adds an audit row to the current unit of work. Data must already be sanitised (codes and ids only).</summary>
    public void Write(Guid? userId, string action, string resourceType, string? resourceId, object? data = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            OccurredAt = clock.GetUtcNow(),
            DataJson = data is null ? null : JsonSerializer.Serialize(data),
        });
}

public sealed partial class WebhookTargetService(
    IAppDbContext db,
    AccessScope scope,
    IWebhookUrlValidator urls,
    ISigningSecretStore secrets,
    AuditLog audit,
    TimeProvider clock,
    IOptions<WebhookOptions> options)
{
    [GeneratedRegex(@"^/(?!.*//)(?!.*\.\.)[A-Za-z0-9/_.\-]{1,200}$")]
    private static partial Regex AdapterPathPattern();

    private WebhookOptions O => options.Value;

    public async Task<IReadOnlyList<WebhookTargetDto>> ListAsync(Guid userId, Guid projectId, CancellationToken ct)
    {
        await scope.RequireProjectAsync(userId, projectId, ct);
        var list = await db.WebhookTargets.AsNoTracking().Where(t => t.ProjectId == projectId && t.RevokedAt == null)
            .OrderBy(t => t.CreatedAt).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<CreatedWebhookTargetDto> CreateAsync(Guid userId, Guid projectId, WebhookTargetRequest request, CancellationToken ct)
    {
        await scope.RequireProjectAsync(userId, projectId, ct);
        var target = ValidateUrl(request.Url);
        var adapter = ValidateAdapterPath(request.AdapterPath);
        if (await db.WebhookTargets.CountAsync(t => t.ProjectId == projectId && t.RevokedAt == null, ct) >= O.MaxTargetsPerProject)
        {
            throw new QuotaExceededException("quota_webhook_targets", $"Bir projede en fazla {O.MaxTargetsPerProject} webhook hedefi olabilir.");
        }

        var secret = WebhookSignature.NewSecret();
        var (cipher, keyId) = secrets.Protect(secret);
        var now = clock.GetUtcNow();
        var entity = new WebhookTarget
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Url = target.Uri.AbsoluteUri,
            Host = target.Host,
            Port = target.Port,
            Path = target.PathAndQuery,
            AdapterPath = adapter,
            Status = WebhookTargetStatus.Pending,
            SigningSecretCiphertext = cipher,
            SigningSecretKeyId = keyId,
            SecretHint = "…" + secret[^4..],
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WebhookTargets.Add(entity);
        audit.Write(userId, "webhook_target.created", "webhook_target", entity.Id.ToString(), new { projectId, devLocal = target.IsDevLocalException });
        await db.SaveChangesAsync(ct);
        return new CreatedWebhookTargetDto(ToDto(entity), secret);
    }

    /// <summary>Changing the URL drops verification: ownership proof is bound to the exact scheme/host/port/path.</summary>
    public async Task<WebhookTargetDto> UpdateAsync(Guid userId, Guid projectId, Guid targetId, WebhookTargetRequest request, CancellationToken ct)
    {
        var entity = await RequireAsync(userId, projectId, targetId, ct);
        if (request.Url is not null)
        {
            var target = ValidateUrl(request.Url);
            if (!string.Equals(target.Uri.AbsoluteUri, entity.Url, StringComparison.Ordinal))
            {
                entity.Url = target.Uri.AbsoluteUri;
                entity.Host = target.Host;
                entity.Port = target.Port;
                entity.Path = target.PathAndQuery;
                ResetVerification(entity);
                audit.Write(userId, "webhook_target.url_changed", "webhook_target", entity.Id.ToString());
            }
        }

        if (request.AdapterPath is not null)
        {
            entity.AdapterPath = request.AdapterPath.Length == 0 ? null : ValidateAdapterPath(request.AdapterPath);
        }

        entity.UpdatedAt = clock.GetUtcNow();
        await SaveAsync(ct);
        return ToDto(entity);
    }

    public async Task<CreatedWebhookTargetDto> RotateSecretAsync(Guid userId, Guid projectId, Guid targetId, CancellationToken ct)
    {
        var entity = await RequireAsync(userId, projectId, targetId, ct);
        var secret = WebhookSignature.NewSecret();
        (entity.SigningSecretCiphertext, entity.SigningSecretKeyId) = secrets.Protect(secret);
        entity.SecretHint = "…" + secret[^4..];
        ResetVerification(entity);
        entity.UpdatedAt = clock.GetUtcNow();
        audit.Write(userId, "webhook_target.secret_rotated", "webhook_target", entity.Id.ToString());
        await SaveAsync(ct);
        return new CreatedWebhookTargetDto(ToDto(entity), secret);
    }

    public async Task DeleteAsync(Guid userId, Guid projectId, Guid targetId, CancellationToken ct)
    {
        var entity = await RequireAsync(userId, projectId, targetId, ct);
        var now = clock.GetUtcNow();
        entity.RevokedAt = now;
        entity.Status = WebhookTargetStatus.Revoked;
        entity.UpdatedAt = now;
        audit.Write(userId, "webhook_target.deleted", "webhook_target", entity.Id.ToString());
        await SaveAsync(ct);
        await CancelPendingDeliveriesAsync(db, now, ct, targetId: targetId);
    }

    /// <summary>Queues an ownership check. Only the worker makes outbound calls (it alone needs internet egress).</summary>
    public async Task<WebhookTargetDto> RequestVerificationAsync(Guid userId, Guid projectId, Guid targetId, CancellationToken ct)
    {
        var entity = await RequireAsync(userId, projectId, targetId, ct);
        var now = clock.GetUtcNow();
        if (entity.LastVerificationAttemptAt is { } last && now - last < TimeSpan.FromSeconds(O.MinSecondsBetweenVerifications))
        {
            throw new QuotaExceededException("verification_rate_limited", $"Doğrulama en fazla {O.MinSecondsBetweenVerifications} saniyede bir istenebilir.");
        }

        entity.LastVerificationAttemptAt = now;
        entity.UpdatedAt = now;
        db.OutboxJobs.Add(new OutboxJob
        {
            Id = Guid.NewGuid(),
            Type = JobTypes.VerifyTarget,
            PayloadJson = JsonSerializer.Serialize(new { targetId, requestedBy = userId }),
            Status = OutboxStatus.Pending,
            MaxAttempts = 1,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        audit.Write(userId, "webhook_target.verification_requested", "webhook_target", entity.Id.ToString());
        await SaveAsync(ct);
        return ToDto(entity);
    }

    public static async Task CancelPendingDeliveriesAsync(IAppDbContext db, DateTimeOffset now, CancellationToken ct, Guid? runId = null, Guid? targetId = null)
    {
        var deliveries = db.WebhookDeliveryAttempts.Where(d => d.Outcome == DeliveryAttemptOutcome.Pending);
        if (runId is { } r)
        {
            deliveries = deliveries.Where(d => d.RunId == r);
        }

        if (targetId is { } t)
        {
            deliveries = deliveries.Where(d => d.TargetId == t);
        }

        var runIds = deliveries.Select(d => d.RunId).Distinct();
        await db.OutboxJobs.Where(j => j.Status == OutboxStatus.Pending && j.Type == JobTypes.DeliverWebhook && j.RunId != null && runIds.Contains(j.RunId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, OutboxStatus.Cancelled).SetProperty(j => j.UpdatedAt, now), ct);
        await deliveries.ExecuteUpdateAsync(u => u.SetProperty(d => d.Outcome, DeliveryAttemptOutcome.Cancelled).SetProperty(d => d.CompletedAt, now), ct);
    }

    private static void ResetVerification(WebhookTarget entity)
    {
        entity.Status = WebhookTargetStatus.Pending;
        entity.VerifiedAt = null;
        entity.VerificationExpiresAt = null;
        entity.LastVerificationError = null;
    }

    private async Task<WebhookTarget> RequireAsync(Guid userId, Guid projectId, Guid targetId, CancellationToken ct)
    {
        await scope.RequireProjectAsync(userId, projectId, ct);
        return await db.WebhookTargets.FirstOrDefaultAsync(t => t.Id == targetId && t.ProjectId == projectId && t.RevokedAt == null, ct)
            ?? throw new NotFoundException("Webhook hedefi");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("concurrent_update", "Hedef başka bir istekle değiştirildi; sayfayı yenileyin.");
        }
    }

    private ValidatedTarget ValidateUrl(string? url)
    {
        var (target, error) = urls.Validate(url);
        return target ?? throw ValidationFailedException.For("url", Messages.GetValueOrDefault(error ?? "", "URL kabul edilmedi."));
    }

    private static string? ValidateAdapterPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return AdapterPathPattern().IsMatch(path)
            ? path
            : throw ValidationFailedException.For("adapterPath", "Adapter yolu '/' ile başlamalı; harf, rakam, '/', '_', '-', '.' içerebilir ('..' ve '//' yok).");
    }

    private static readonly Dictionary<string, string> Messages = new()
    {
        ["url_invalid"] = "Geçerli bir mutlak URL girin.",
        ["url_control_characters"] = "URL boşluk veya kontrol karakteri içeremez.",
        ["url_userinfo_not_allowed"] = "URL içinde kullanıcı adı/parola olamaz.",
        ["url_fragment_not_allowed"] = "URL '#' parçası içeremez.",
        ["url_https_required"] = "Yalnızca HTTPS hedefler kabul edilir.",
        ["url_port_not_allowed"] = "Bu port izinli değil (443 veya 8443).",
        ["url_ip_literal_not_allowed"] = "IP adresi yerine alan adı kullanın.",
        ["url_host_invalid"] = "Geçerli bir alan adı girin.",
        ["url_host_not_allowed"] = "Bu alan adı (yerel/iç ağ) kabul edilmez.",
    };

    private WebhookTargetDto ToDto(WebhookTarget t) => new(t.Id, t.Url, t.AdapterPath,
        t.Status == WebhookTargetStatus.Verified && t.VerificationExpiresAt <= clock.GetUtcNow() ? WebhookTargetStatus.Pending : t.Status,
        t.SecretHint, t.VerifiedAt, t.VerificationExpiresAt, t.LastVerificationAttemptAt, t.LastVerificationError, t.CreatedAt,
        urls.Validate(t.Url).Target?.IsDevLocalException ?? false);
}
