using APIArena.Domain.Entities;
using APIArena.Domain.Evaluation;
using APIArena.Domain.Scenarios;

namespace APIArena.UnitTests;

public sealed class EvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static int _seq;

    private static ObservationFact Obs(string? op, int attempt, double atMs, int status, DeliveryOutcome outcome = DeliveryOutcome.Sent, int? duration = 5, int delay = 0) =>
        new(Interlocked.Increment(ref _seq), op, attempt, T0.AddMilliseconds(atMs), status, outcome, duration, delay, null);

    private static EvaluationResult Run(BuiltInScenario s, RunStatus status, params ObservationFact[] obs) =>
        Evaluator.Evaluate(new EvaluationInput(s.Engine, ScenarioJson.Serialize(s.Definition), s.EvaluatorVersion, status, null, obs));

    private static CheckStatus Of(EvaluationResult r, string id) => r.Checks.Single(c => c.Id == id).Status;

    // ---------------- retry-503 ----------------

    [Fact]
    public void Correct_retry_client_passes_every_retry_check_with_evidence()
    {
        var r = Run(BuiltInScenarios.Retry503, RunStatus.Completed,
            Obs("order-1", 1, 0, 503), Obs("order-1", 2, 300, 503), Obs("order-1", 3, 900, 200));
        Assert.All(r.Checks, c => Assert.Equal(CheckStatus.Passed, c.Status));
        Assert.All(r.Checks, c => Assert.NotEmpty(c.Evidence));
        Assert.All(r.Checks, c => Assert.Equal(EvidenceKind.Observed, c.EvidenceKind));
        Assert.Equal("retry-503@1.0", r.EvaluatorVersion);
    }

    [Fact]
    public void Client_that_gives_up_fails_on_completed_run()
    {
        var r = Run(BuiltInScenarios.Retry503, RunStatus.Completed, Obs("order-1", 1, 0, 503));
        Assert.Equal(CheckStatus.Failed, Of(r, "retried-after-503"));
        Assert.Equal(CheckStatus.Failed, Of(r, "reached-success"));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "backoff-between-retries"));
        Assert.Equal(CheckStatus.Passed, Of(r, "idempotency-key-present"));
    }

    [Theory]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.Expired)]
    [InlineData(RunStatus.Failed)]
    public void Incomplete_runs_turn_absence_failures_into_inconclusive_and_never_into_passed(RunStatus status)
    {
        var r = Run(BuiltInScenarios.Retry503, status, Obs("order-1", 1, 0, 503));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "retried-after-503"));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "reached-success"));
        Assert.NotNull(r.IncompleteReason);
        Assert.DoesNotContain(r.Checks, c => c.Status == CheckStatus.Passed && c.Id is "reached-success" or "retried-after-503");
    }

    [Fact]
    public void Quota_exceeded_is_incomplete_but_positive_evidence_failures_stay_failed()
    {
        var obs = new[] { Obs(null, 1, 0, 503), Obs(null, 2, 5, 503) };
        var r = Evaluator.Evaluate(new EvaluationInput(Engines.Retry503, ScenarioJson.Serialize(BuiltInScenarios.Retry503.Definition), "retry-503@1.0",
            RunStatus.Completed, "quota_exceeded", obs));
        Assert.Equal("quota_exceeded", r.IncompleteReason);
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "reached-success"));
        Assert.Equal(CheckStatus.Failed, Of(r, "idempotency-key-present")); // observed, not absence-based
        Assert.Equal(CheckStatus.Failed, Of(r, "backoff-between-retries"));
    }

    [Theory]
    [InlineData(200, CheckStatus.Passed)]
    [InlineData(199.9, CheckStatus.Failed)]
    [InlineData(10, CheckStatus.Failed)]
    public void Backoff_boundary_is_exactly_the_configured_minimum(double gapMs, CheckStatus expected)
    {
        var r = Run(BuiltInScenarios.Retry503, RunStatus.Completed,
            Obs("k", 1, 0, 503), Obs("k", 2, gapMs, 503), Obs("k", 3, gapMs * 2, 200));
        Assert.Equal(expected, Of(r, "backoff-between-retries"));
    }

    [Fact]
    public void Separate_operations_are_not_merged_into_one_retry_group()
    {
        var r = Run(BuiltInScenarios.Retry503, RunStatus.Completed,
            Obs("A", 1, 0, 503), Obs("B", 1, 50, 503), Obs("A", 2, 400, 503), Obs("A", 3, 900, 200));
        var retried = r.Checks.Single(c => c.Id == "retried-after-503");
        Assert.Equal(CheckStatus.Failed, retried.Status); // B was abandoned even though A succeeded
        Assert.Single(retried.Evidence);
        Assert.Equal(CheckStatus.Passed, Of(r, "reached-success"));
    }

    [Fact]
    public void Read_order_does_not_change_the_result()
    {
        var a = Obs("k", 1, 0, 503);
        var b = Obs("k", 2, 300, 503);
        var c = Obs("k", 3, 700, 200);
        var sorted = Run(BuiltInScenarios.Retry503, RunStatus.Completed, a, b, c);
        var shuffled = Run(BuiltInScenarios.Retry503, RunStatus.Completed, c, a, b);
        Assert.Equal(sorted.Checks.Select(x => (x.Id, x.Status)), shuffled.Checks.Select(x => (x.Id, x.Status)));
    }

    [Fact]
    public void Unconfirmed_503_cannot_prove_retry_behaviour()
    {
        var r = Run(BuiltInScenarios.Retry503, RunStatus.Completed, Obs("k", 1, 0, 503, DeliveryOutcome.Planned), Obs("k", 2, 10, 503, DeliveryOutcome.Unknown));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "retried-after-503"));
    }

    [Fact]
    public void No_traffic_yields_no_passed_check()
    {
        foreach (var s in BuiltInScenarios.All)
        {
            var r = Run(s, RunStatus.Completed);
            Assert.DoesNotContain(r.Checks, c => c.Status == CheckStatus.Passed);
        }
    }

    [Fact]
    public void Retry_after_success_and_unbounded_retries_fail()
    {
        var obs = new List<ObservationFact> { Obs("k", 1, 0, 503), Obs("k", 2, 300, 503), Obs("k", 3, 600, 200) };
        for (var i = 4; i <= 8; i++)
        {
            obs.Add(Obs("k", i, i * 300, 200));
        }

        var r = Run(BuiltInScenarios.Retry503, RunStatus.Completed, [.. obs]);
        Assert.Equal(CheckStatus.Failed, Of(r, "no-retry-after-success"));
        Assert.Equal(CheckStatus.Failed, Of(r, "bounded-retries"));
    }

    // ---------------- rate-limit ----------------

    [Theory]
    [InlineData(1900, CheckStatus.Passed)]   // 2000 ms − 100 ms tolerance, exactly on the boundary
    [InlineData(1899, CheckStatus.Failed)]
    [InlineData(2500, CheckStatus.Passed)]
    public void Retry_after_boundary_uses_server_receipt_times(double nextMs, CheckStatus expected)
    {
        var r = Run(BuiltInScenarios.RateLimit, RunStatus.Completed, Obs(null, 1, 0, 429), Obs(null, 2, nextMs, nextMs >= 2000 ? 200 : 429));
        Assert.Equal(expected, Of(r, "respected-retry-after"));
    }

    [Fact]
    public void Expected_429_alone_is_not_a_failure_and_hammering_is()
    {
        var polite = Run(BuiltInScenarios.RateLimit, RunStatus.Completed, Obs(null, 1, 0, 429), Obs(null, 2, 2100, 200));
        Assert.All(polite.Checks, c => Assert.Equal(CheckStatus.Passed, c.Status));

        var hammer = Run(BuiltInScenarios.RateLimit, RunStatus.Completed,
            Obs(null, 1, 0, 429), Obs(null, 2, 10, 429), Obs(null, 3, 20, 429), Obs(null, 4, 30, 429), Obs(null, 5, 2050, 200));
        Assert.Equal(CheckStatus.Failed, Of(hammer, "no-hammering"));
        Assert.Equal(CheckStatus.Failed, Of(hammer, "respected-retry-after"));
        Assert.Equal(CheckStatus.Passed, Of(hammer, "eventual-success"));
    }

    [Fact]
    public void No_request_after_429_is_inconclusive_for_retry_after()
    {
        var r = Run(BuiltInScenarios.RateLimit, RunStatus.Completed, Obs(null, 1, 0, 429));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "respected-retry-after"));
        Assert.Equal(CheckStatus.Failed, Of(r, "eventual-success"));
    }

    // ---------------- timeout ----------------

    [Fact]
    public void Disconnect_before_delay_is_transport_evidence_and_ux_stays_inconclusive()
    {
        var r = Run(BuiltInScenarios.Timeout, RunStatus.Completed, Obs(null, 1, 0, 200, DeliveryOutcome.ClientDisconnected, 1010, 3000));
        Assert.Equal(CheckStatus.Passed, Of(r, "client-timeout-observed"));
        Assert.Equal(CheckStatus.Passed, Of(r, "timeout-within-budget"));
        Assert.Equal(CheckStatus.Passed, Of(r, "bounded-retries"));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "client-error-handling"));
    }

    [Fact]
    public void Waiting_the_full_delay_fails_and_unknown_is_inconclusive()
    {
        var waited = Run(BuiltInScenarios.Timeout, RunStatus.Completed, Obs(null, 1, 0, 200, DeliveryOutcome.Sent, 3004, 3000));
        Assert.Equal(CheckStatus.Failed, Of(waited, "client-timeout-observed"));

        var unknown = Run(BuiltInScenarios.Timeout, RunStatus.Completed, Obs(null, 1, 0, 200, DeliveryOutcome.Unknown, null, 3000));
        Assert.Equal(CheckStatus.Inconclusive, Of(unknown, "client-timeout-observed"));
    }

    [Theory]
    [InlineData(2000, CheckStatus.Passed)]
    [InlineData(2001, CheckStatus.Failed)]
    public void Timeout_budget_boundary(int disconnectMs, CheckStatus expected)
    {
        var r = Run(BuiltInScenarios.Timeout, RunStatus.Completed, Obs(null, 1, 0, 200, DeliveryOutcome.ClientDisconnected, disconnectMs, 3000));
        Assert.Equal(expected, Of(r, "timeout-within-budget"));
    }

    // ---------------- custom & webhook ----------------

    [Fact]
    public void Custom_scenario_checks_and_not_applicable()
    {
        var def = new CustomScenarioDefinition(1, "GET", "items", [new CustomStep(500, 0, null, null), new CustomStep(200, 0, new CustomBody("json", "{}"), null)]);
        var r = Evaluator.Evaluate(new EvaluationInput(Engines.Custom, ScenarioJson.Serialize(def), "custom@1.0", RunStatus.Completed, null,
            [Obs("x", 1, 0, 500), Obs("x", 2, 400, 200)]));
        Assert.Equal(CheckStatus.Passed, Of(r, "all-steps-exercised"));
        Assert.Equal(CheckStatus.NotApplicable, Of(r, "retry-after-respected"));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "business-outcome"));
    }

    [Fact]
    public void Webhook_200_without_adapter_is_not_business_correctness()
    {
        var def = new DuplicateWebhookDefinition("payment.succeeded", 4200, "TRY", 2, true);
        var deliveries = new[]
        {
            new DeliveryFact(Guid.NewGuid(), "evt_1", "pay_1", 1, 1, 1, T0, 200, "Delivered"),
            new DeliveryFact(Guid.NewGuid(), "evt_1", "pay_1", 1, 2, 1, T0, 200, "Delivered"),
        };
        var r = Evaluator.Evaluate(new EvaluationInput(Engines.DuplicateWebhook, ScenarioJson.Serialize(def), "duplicate-webhook@1.0", RunStatus.Completed, null, [], deliveries));
        Assert.Equal(CheckStatus.Passed, Of(r, "deliveries-acknowledged"));
        Assert.Equal(CheckStatus.Inconclusive, Of(r, "processed-exactly-once"));

        var withAdapter = Evaluator.Evaluate(new EvaluationInput(Engines.DuplicateWebhook, ScenarioJson.Serialize(def), "duplicate-webhook@1.0", RunStatus.Completed, null, [], deliveries,
            new AdapterSnapshot([new AdapterAggregate("pay_1", "succeeded", 1, 4200)], [new AdapterEventCount("evt_1", 2)])));
        var check = withAdapter.Checks.Single(c => c.Id == "processed-exactly-once");
        Assert.Equal(CheckStatus.Failed, check.Status);
        Assert.Equal(EvidenceKind.AdapterReported, check.EvidenceKind);
    }
}
