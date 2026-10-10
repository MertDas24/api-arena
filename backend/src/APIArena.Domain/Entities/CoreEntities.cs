namespace APIArena.Domain.Entities;

public sealed class Project
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Request body previews are OFF by default; when on, sensitive fields are masked before storage.</summary>
    public bool CaptureBodies { get; set; }

    /// <summary>Exact browser origins allowed to call this project's simulator URLs (no credentials, no wildcard). Newline separated.</summary>
    public string SimulatorAllowedOrigins { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft delete (ADR-011). Hidden everywhere; purged by retention.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public IReadOnlyList<string> AllowedOrigins() =>
        SimulatorAllowedOrigins.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public enum ScenarioKind
{
    BuiltIn = 0,
    Custom = 1,
}

public sealed class ScenarioTemplate
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = "";
    public ScenarioKind Kind { get; set; }

    /// <summary>Null for built-in templates; owner for private custom templates.</summary>
    public Guid? OwnerId { get; set; }

    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<ScenarioVersion> Versions { get; set; } = [];
}

/// <summary>Immutable once written: runs snapshot their version; edits create a new version row.</summary>
public sealed class ScenarioVersion
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public int Version { get; set; }

    /// <summary>Engine key: retry-503, rate-limit, timeout, duplicate-webhook, out-of-order-webhook, custom.</summary>
    public string Engine { get; set; } = "";

    public string EvaluatorVersion { get; set; } = "";
    public string DefinitionJson { get; set; } = "{}";

    /// <summary>Teaching content: description, learning goal, setup, expected trace, rules, limits, curl, connection.</summary>
    public string ContentJson { get; set; } = "{}";

    public string ContentHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public enum RunStatus
{
    Created = 0,
    Running = 1,
    Completed = 2,
    Cancelled = 3,
    Expired = 4,
    Failed = 5,
}

public sealed class Run
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ScenarioVersionId { get; set; }

    // Snapshot of what the run was created against (RUN-03); later template edits never change these.
    public string ScenarioSlug { get; set; } = "";
    public int ScenarioVersionNumber { get; set; }
    public string Engine { get; set; } = "";
    public string EvaluatorVersion { get; set; } = "";
    public string ConfigSnapshotJson { get; set; } = "{}";
    public int Seed { get; set; }
    public int InboundLimit { get; set; }
    public int TtlSeconds { get; set; }

    public string? Label { get; set; }
    public RunStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Atomically incremented per run; orders every event (requests and outcomes).</summary>
    public long NextSequence { get; set; }

    public int InboundCount { get; set; }
    public bool QuotaExceeded { get; set; }
    public DateTimeOffset? RateLimitedUntil { get; set; }

    /// <summary>Why evidence may be incomplete: quota, cancelled, expired, platform failure.</summary>
    public string? IncompleteReason { get; set; }

    public Guid? WebhookTargetId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class RunOperation
{
    public Guid RunId { get; set; }
    public string OperationKey { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public enum DeliveryOutcome
{
    /// <summary>Decision recorded, response not (yet) confirmed. After finalisation treated as Unknown.</summary>
    Planned = 0,
    Sent = 1,
    ClientDisconnected = 2,
    Unknown = 3,
    Rejected = 4,
}

public sealed class RequestObservation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public long Sequence { get; set; }

    /// <summary>Client-supplied Idempotency-Key (validated, max 128 chars) or null when absent.</summary>
    public string? OperationKey { get; set; }

    public int Attempt { get; set; }
    public string Method { get; set; } = "";
    public string Route { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public int PlannedStatus { get; set; }
    public int PlannedDelayMs { get; set; }
    public DeliveryOutcome Outcome { get; set; }
    public int? DurationMs { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>JSON object of allow-listed headers; sensitive values replaced by [REDACTED] before storage.</summary>
    public string HeadersJson { get; set; } = "{}";

    public int BodyBytes { get; set; }
    public string? BodyPreview { get; set; }
    public bool BodyTruncated { get; set; }
    public string? RejectReason { get; set; }
    public string? ResponsePreview { get; set; }
}

public sealed class RunEvent
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ObservationId { get; set; }

    /// <summary>Small, already-sanitised JSON payload. Never contains secrets.</summary>
    public string? DataJson { get; set; }
}

public sealed class RunAccessKey
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>SHA-256 of the full key. The plaintext is shown once at creation and never stored.</summary>
    public byte[] KeyHash { get; set; } = [];

    public string DisplayPrefix { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public enum CheckStatus
{
    Passed = 0,
    Failed = 1,
    Inconclusive = 2,
    NotApplicable = 3,
}

public enum EvidenceKind
{
    /// <summary>Platform's own observation of traffic.</summary>
    Observed = 0,

    /// <summary>Reported by the user's adapter — produced by the user's system, not independently verified.</summary>
    AdapterReported = 1,

    /// <summary>Client's own claim; never counts as independent verification.</summary>
    SelfReported = 2,

    /// <summary>No evidence available (used with Inconclusive / NotApplicable).</summary>
    None = 3,
}

public sealed class ReportSnapshot
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ScenarioSlug { get; set; } = "";
    public int ScenarioVersion { get; set; }
    public string EvaluatorVersion { get; set; } = "";
    public RunStatus RunFinalStatus { get; set; }
    public string? IncompleteReason { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Inconclusive { get; set; }
    public int NotApplicable { get; set; }
    public int RequestCount { get; set; }

    /// <summary>Full immutable report document (checks, timeline summary, unverifiable list).</summary>
    public string ReportJson { get; set; } = "{}";

    public List<CheckResult> Checks { get; set; } = [];
}

public sealed class CheckResult
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public string CheckId { get; set; } = "";
    public string Title { get; set; } = "";
    public CheckStatus Status { get; set; }
    public EvidenceKind EvidenceKind { get; set; }
    public string Expected { get; set; } = "";
    public string Observed { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string? Suggestion { get; set; }

    /// <summary>JSON array of evidence event sequences.</summary>
    public string EvidenceJson { get; set; } = "[]";

    public string EvaluatorVersion { get; set; } = "";
    public int Order { get; set; }
}
