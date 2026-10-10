using System.Diagnostics.Metrics;

namespace APIArena.Application.Ops;

/// <summary>
/// Low-cardinality metrics (OPS-05) on the "APIArena" meter. Tags are small fixed vocabularies only — never run ids,
/// user ids, routes, bodies, keys or hosts. Export is deployment-specific (dotnet-counters / OpenTelemetry collector);
/// nothing is exported unless an operator wires a receiver (see docs/runbooks/OPERATIONS.md).
/// </summary>
public static class ArenaMetrics
{
    public const string MeterName = "APIArena";
    private static readonly Meter Meter = new(MeterName, "1.0");

    private static readonly Counter<long> SimulatorRequests = Meter.CreateCounter<long>("apiarena.simulator.requests", description: "Simulator requests by result (scenario|rejected|platform)");
    private static readonly Counter<long> QuotaRejections = Meter.CreateCounter<long>("apiarena.quota.rejections", description: "Requests refused by platform limits");
    private static readonly Counter<long> SsrfBlocked = Meter.CreateCounter<long>("apiarena.outbound.blocked", description: "Outbound requests blocked by URL/IP policy");
    private static readonly Counter<long> WebhookDeliveries = Meter.CreateCounter<long>("apiarena.webhook.deliveries", description: "Webhook delivery attempts by outcome");
    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>("apiarena.worker.jobs", description: "Outbox jobs by type and result");
    private static readonly Histogram<double> PlanDuration = Meter.CreateHistogram<double>("apiarena.simulator.plan_ms", unit: "ms", description: "Time to plan a simulated response (DB transaction)");

    private static long _activeRuns;
    private static double _outboxLagSeconds;

    static ArenaMetrics()
    {
        Meter.CreateObservableGauge("apiarena.runs.active", () => Interlocked.Read(ref _activeRuns), description: "Running runs (sampled by the worker)");
        Meter.CreateObservableGauge("apiarena.worker.outbox_lag_seconds", () => Volatile.Read(ref _outboxLagSeconds), unit: "s", description: "Age of the oldest due pending job");
    }

    private static readonly HashSet<string> Results = ["scenario", "rejected", "platform"];

    public static void SimulatorRequest(string result) => SimulatorRequests.Add(1, new KeyValuePair<string, object?>("result", Results.Contains(result) ? result : "other"));

    public static void Quota(string kind) => QuotaRejections.Add(1, new KeyValuePair<string, object?>("kind", kind.Length <= 32 ? kind : "other"));

    public static void OutboundBlocked(string reason) => SsrfBlocked.Add(1, new KeyValuePair<string, object?>("reason", reason.Length <= 32 ? reason : "other"));

    public static void WebhookDelivery(string outcome) => WebhookDeliveries.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public static void Job(string type, string result) =>
        Jobs.Add(1, new KeyValuePair<string, object?>("type", type.Length <= 32 ? type : "other"), new KeyValuePair<string, object?>("result", result));

    public static void PlanMs(double ms) => PlanDuration.Record(ms);

    public static void Sample(long activeRuns, double outboxLagSeconds)
    {
        Interlocked.Exchange(ref _activeRuns, activeRuns);
        Volatile.Write(ref _outboxLagSeconds, outboxLagSeconds);
    }
}
