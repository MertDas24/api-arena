using System.Diagnostics;

namespace APIArena.Infrastructure.Time;

/// <summary>
/// UTC clock anchored once at startup and advanced with <see cref="Stopwatch"/>, so that intervals measured
/// within a process (retry spacing, Retry-After windows, delays) never jump with NTP or manual clock changes.
/// Cross-instance comparison still relies on hosts being NTP-synchronised (documented in ADR-012).
/// </summary>
public sealed class MonotonicUtcTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _anchorUtc = DateTimeOffset.UtcNow;
    private readonly long _anchorTimestamp = Stopwatch.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => _anchorUtc + Stopwatch.GetElapsedTime(_anchorTimestamp);
}
