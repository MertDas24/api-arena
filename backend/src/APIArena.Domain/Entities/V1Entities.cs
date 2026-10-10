namespace APIArena.Domain.Entities;

public enum WebhookTargetStatus
{
    Pending = 0,
    Verified = 1,
    VerificationFailed = 2,
    Revoked = 3,
}

/// <summary>
/// A user-declared HTTPS endpoint. Verification is bound to the exact scheme/host/port/path: changing the URL resets
/// it to Pending. Being verified never allows private/internal addresses — every connection is re-checked.
/// </summary>
public sealed class WebhookTarget
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Path { get; set; } = "/";

    /// <summary>Optional read-only adapter path on the same origin (C7), e.g. /webhooks/apiarena/adapter.</summary>
    public string? AdapterPath { get; set; }

    public WebhookTargetStatus Status { get; set; }

    /// <summary>AES-GCM ciphertext of the signing secret (nonce|tag|ct). The key lives outside the DB and repo.</summary>
    public byte[] SigningSecretCiphertext { get; set; } = [];

    public string SigningSecretKeyId { get; set; } = "";
    public string SecretHint { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? VerificationExpiresAt { get; set; }
    public DateTimeOffset? LastVerificationAttemptAt { get; set; }
    public string? LastVerificationError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public enum DeliveryAttemptOutcome
{
    Pending = 0,
    Delivered = 1,
    Failed = 2,
    TimedOut = 3,
    Blocked = 4,
    Cancelled = 5,
    Unknown = 6,
}

/// <summary>One HTTP delivery attempt. Retries/replays get a new DeliveryId but keep the logical EventId.</summary>
public sealed class WebhookDeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid TargetId { get; set; }
    public string EventId { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public int EventVersion { get; set; }
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public int PlannedOrder { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ResponseStatus { get; set; }
    public int? DurationMs { get; set; }
    public DeliveryAttemptOutcome Outcome { get; set; }
    public string? Error { get; set; }
}

public enum OutboxStatus
{
    Pending = 0,
    Leased = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>SQL-backed durable job (no Redis). Claimed with a lease; an expired lease makes the job claimable again.</summary>
public sealed class OutboxJob
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public Guid? RunId { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string? ResourceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Small sanitised JSON (codes, ids). Never secrets, bodies or IP addresses of targets.</summary>
    public string? DataJson { get; set; }
}

public sealed class ShareLink
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public string DisplayPrefix { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int ViewCount { get; set; }
}
