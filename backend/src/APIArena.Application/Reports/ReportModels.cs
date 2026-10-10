using APIArena.Domain.Entities;

namespace APIArena.Application.Reports;

public sealed record ReportCounts(int Passed, int Failed, int Inconclusive, int NotApplicable)
{
    public int Applicable => Passed + Failed + Inconclusive;
    public int Total => Applicable + NotApplicable;
}

public sealed record ReportCheck(
    string Id,
    string Title,
    CheckStatus Status,
    EvidenceKind EvidenceKind,
    string Expected,
    string Observed,
    string Explanation,
    string? Suggestion,
    IReadOnlyList<long> Evidence);

public sealed record TimelineEntry(
    long Sequence,
    DateTimeOffset ReceivedAt,
    long OffsetMs,
    string Method,
    string Route,
    int Attempt,
    string? OperationKey,
    int PlannedStatus,
    DeliveryOutcome Outcome,
    int? DurationMs,
    string? RejectReason);

public sealed record DeliveryEntry(Guid DeliveryId, string EventId, int EventVersion, int PlannedOrder, int AttemptNumber, DateTimeOffset? SentAt, int? ResponseStatus, string Outcome);

/// <summary>The immutable report document stored in ReportSnapshots.ReportJson (REP-03).</summary>
public sealed record ReportDocument(
    Guid ReportId,
    Guid RunId,
    Guid ProjectId,
    string ProjectName,
    string ScenarioSlug,
    int ScenarioVersion,
    string ScenarioTitle,
    string Engine,
    string EvaluatorVersion,
    RunStatus RunStatus,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long? RunDurationMs,
    string? IncompleteReason,
    string? IncompleteReasonText,
    DateTimeOffset GeneratedAt,
    ReportCounts Counts,
    int RequestCount,
    int RejectedCount,
    int InboundLimit,
    IReadOnlyList<ReportCheck> Checks,
    IReadOnlyList<string> Unverifiable,
    IReadOnlyList<TimelineEntry> Timeline,
    bool TimelineTruncated,
    IReadOnlyList<DeliveryEntry> Deliveries,
    bool AdapterUsed,
    string Notice);

public sealed record CompareCheckRow(string Id, string Title, CheckStatus? Left, CheckStatus? Right, string Change);

public sealed record CompareSide(Guid RunId, string ScenarioSlug, int ScenarioVersion, string EvaluatorVersion, RunStatus RunStatus, string? Label,
    DateTimeOffset CreatedAt, ReportCounts Counts, int RequestCount, long? RunDurationMs, string? IncompleteReason);

public sealed record CompareResult(CompareSide Left, CompareSide Right, bool Comparable, IReadOnlyList<string> Warnings, IReadOnlyList<CompareCheckRow> Checks);
