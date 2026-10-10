using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIArena.Domain.Entities;
using APIArena.Infrastructure.Jobs;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.IntegrationTests;

public sealed class Stage13_14_WebhookTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TestUser User, Guid Project, Guid Target, string Secret)> TargetAsync(WebhookHarness h, string? adapterPath = "/webhooks/apiarena/adapter")
    {
        var user = await TestUser.CreateVerifiedAsync(h.Api);
        var project = await Arrange.ProjectAsync(user);
        var created = await user.PostAsync($"/api/projects/{project}/webhook-targets", new { url = h.ReceiverUrl, adapterPath });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var json = await TestUser.JsonAsync(created);
        return (user, project, json.GetProperty("target").GetProperty("id").GetGuid(), json.GetProperty("signingSecret").GetString()!);
    }

    private static async Task<(TestUser User, Guid Project, Guid Target)> VerifiedTargetAsync(WebhookHarness h, string? adapterPath = "/webhooks/apiarena/adapter")
    {
        var (user, project, target, secret) = await TargetAsync(h, adapterPath);
        h.ReceiverOptions.SigningSecret = secret;
        Assert.Equal(HttpStatusCode.Accepted, (await user.PostAsync($"/api/projects/{project}/webhook-targets/{target}/verify")).StatusCode);
        await h.DrainAsync();
        var list = await user.GetJsonAsync($"/api/projects/{project}/webhook-targets");
        Assert.Equal("Verified", list[0].GetProperty("status").GetString());
        return (user, project, target);
    }

    private static async Task<JsonElement> RunWebhookScenarioAsync(WebhookHarness h, TestUser user, Guid project, Guid target, string scenario)
    {
        var created = await user.PostAsync($"/api/projects/{project}/runs", new { scenario, webhookTargetId = target });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var run = (await TestUser.JsonAsync(created)).GetProperty("id").GetGuid();
        await Arrange.StartAsync(user, run);
        for (var i = 0; i < 4; i++)
        {
            h.Api.Clock.Advance(TimeSpan.FromSeconds(1.5));
            await h.DrainAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/runs/{run}/finish")).StatusCode);
        await h.DrainAsync(); // adapter fetch + finalisation happen in the worker
        return await user.GetJsonAsync($"/api/runs/{run}/report");
    }

    private static Dictionary<string, (string Status, string Evidence)> Checks(JsonElement report) =>
        report.GetProperty("checks").EnumerateArray().ToDictionary(
            c => c.GetProperty("id").GetString()!, c => (c.GetProperty("status").GetString()!, c.GetProperty("evidenceKind").GetString()!));

    [Fact]
    public async Task Webhook_module_is_unreachable_when_flag_is_off()
    {
        await using var api = new ApiFactory(db); // default: ApiArena:Webhooks:Enabled = false
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        Assert.False((await user.GetJsonAsync("/api/features")).GetProperty("webhooks").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/projects/{project}/webhook-targets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync($"/api/projects/{project}/webhook-targets", new { url = "https://hooks.customer-app.net/x" })).StatusCode);
        var scenarios = (await user.GetJsonAsync("/api/scenarios")).EnumerateArray().Select(s => s.GetProperty("slug").GetString()).ToList();
        Assert.DoesNotContain("duplicate-webhook-v1", scenarios);
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsync($"/api/projects/{project}/runs", new { scenario = "duplicate-webhook-v1" })).StatusCode);
    }

    [Theory]
    [InlineData("http://hooks.customer-app.net/x")]
    [InlineData("https://localhost/x")]
    [InlineData("https://10.0.0.1/x")]
    [InlineData("https://user:pw@hooks.customer-app.net/x")]
    [InlineData("https://hooks.customer-app.net:22/x")]
    public async Task Unsafe_target_urls_are_rejected(string url)
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var user = await TestUser.CreateVerifiedAsync(h.Api);
        var project = await Arrange.ProjectAsync(user);
        var r = await user.PostAsync($"/api/projects/{project}/webhook-targets", new { url });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Secret_is_shown_once_encrypted_at_rest_and_targets_are_owner_scoped()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target, secret) = await TargetAsync(h);
        Assert.StartsWith("whsec_", secret);
        var list = await user.GetJsonAsync($"/api/projects/{project}/webhook-targets");
        Assert.DoesNotContain(secret, list.ToString());
        Assert.Equal("…" + secret[^4..], list[0].GetProperty("secretHint").GetString());

        await using (var ctx = db.CreateContext())
        {
            var row = await ctx.WebhookTargets.SingleAsync(t => t.Id == target, Ct);
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(row.SigningSecretCiphertext));
            Assert.Equal("test-k1", row.SigningSecretKeyId);
        }

        var other = await TestUser.CreateVerifiedAsync(h.Api);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/projects/{project}/webhook-targets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/projects/{project}/webhook-targets/{target}/verify")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(HttpMethod.Delete, $"/api/projects/{project}/webhook-targets/{target}")).StatusCode);
        var otherProject = await Arrange.ProjectAsync(other);
        var steal = await other.PostAsync($"/api/projects/{otherProject}/runs", new { scenario = "duplicate-webhook-v1", webhookTargetId = target });
        Assert.Equal(HttpStatusCode.BadRequest, steal.StatusCode);
    }

    [Fact]
    public async Task Verification_requires_hmac_proof_is_rate_limited_and_url_change_resets_it()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target, _) = await TargetAsync(h);
        h.ReceiverOptions.SigningSecret = "whsec_wrong-secret-held-by-someone-else";
        await user.PostAsync($"/api/projects/{project}/webhook-targets/{target}/verify");
        await h.DrainAsync();
        var failed = (await user.GetJsonAsync($"/api/projects/{project}/webhook-targets"))[0];
        Assert.Equal("VerificationFailed", failed.GetProperty("status").GetString());

        var tooSoon = await user.PostAsync($"/api/projects/{project}/webhook-targets/{target}/verify");
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);

        // An unverified target cannot be used for runs.
        var run = await user.PostAsync($"/api/projects/{project}/runs", new { scenario = "duplicate-webhook-v1", webhookTargetId = target });
        Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
        Assert.Equal("target_not_verified", await TestUser.ProblemCodeAsync(run));

        var (user2, project2, target2) = await VerifiedTargetAsync(h);
        var patched = await user2.SendAsync(HttpMethod.Patch, $"/api/projects/{project2}/webhook-targets/{target2}",
            new { url = h.ReceiverUrl + "/other-path" });
        Assert.Equal("Pending", (await TestUser.JsonAsync(patched)).GetProperty("status").GetString());

        await using var ctx = db.CreateContext();
        Assert.True(await ctx.AuditEvents.AnyAsync(a => a.ResourceId == target.ToString() && a.Action == "webhook_target.verification_failed", Ct));
    }

    [Fact]
    public async Task Duplicate_webhook_correct_receiver_is_processed_once_with_adapter_evidence()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target) = await VerifiedTargetAsync(h);
        var report = await RunWebhookScenarioAsync(h, user, project, target, "duplicate-webhook-v1");
        var checks = Checks(report);
        Assert.Equal(("Passed", "Observed"), checks["deliveries-acknowledged"]);
        Assert.Equal(("Passed", "Observed"), checks["duplicate-was-delivered"]);
        Assert.Equal(("Passed", "AdapterReported"), checks["processed-exactly-once"]);
        Assert.True(report.GetProperty("adapterUsed").GetBoolean());

        var deliveries = report.GetProperty("deliveries").EnumerateArray().ToList();
        Assert.Equal(2, deliveries.Count);
        Assert.Single(deliveries.Select(d => d.GetProperty("eventId").GetString()).Distinct());
        Assert.Equal(2, deliveries.Select(d => d.GetProperty("deliveryId").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Duplicate_webhook_naive_receiver_fails_and_http_200_is_not_proof()
    {
        await using var h = await WebhookHarness.CreateAsync(db, naive: true);
        var (user, project, target) = await VerifiedTargetAsync(h);
        var checks = Checks(await RunWebhookScenarioAsync(h, user, project, target, "duplicate-webhook-v1"));
        Assert.Equal("Passed", checks["deliveries-acknowledged"].Status); // both got 200 …
        Assert.Equal(("Failed", "AdapterReported"), checks["processed-exactly-once"]); // … yet the payment was applied twice
    }

    [Fact]
    public async Task Without_adapter_business_correctness_is_inconclusive()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target) = await VerifiedTargetAsync(h, adapterPath: null);
        var checks = Checks(await RunWebhookScenarioAsync(h, user, project, target, "duplicate-webhook-v1"));
        Assert.Equal(("Inconclusive", "None"), checks["processed-exactly-once"]);
    }

    [Fact]
    public async Task Out_of_order_correct_receiver_keeps_latest_state_naive_one_does_not()
    {
        await using (var h = await WebhookHarness.CreateAsync(db))
        {
            var (user, project, target) = await VerifiedTargetAsync(h);
            var report = await RunWebhookScenarioAsync(h, user, project, target, "out-of-order-webhook-v1");
            var checks = Checks(report);
            Assert.Equal("Passed", checks["delivered-out-of-order"].Status);
            Assert.Equal(("Passed", "AdapterReported"), checks["final-state-correct"]);
            Assert.Equal(("Passed", "AdapterReported"), checks["stale-event-ignored"]);
            var planned = report.GetProperty("deliveries").EnumerateArray().OrderBy(d => d.GetProperty("plannedOrder").GetInt32()).ToList();
            Assert.Equal(2, planned[0].GetProperty("eventVersion").GetInt32());
        }

        await using (var naive = await WebhookHarness.CreateAsync(db, naive: true))
        {
            var (user, project, target) = await VerifiedTargetAsync(naive);
            var checks = Checks(await RunWebhookScenarioAsync(naive, user, project, target, "out-of-order-webhook-v1"));
            Assert.Equal("Failed", checks["final-state-correct"].Status);
            Assert.Equal("Failed", checks["stale-event-ignored"].Status);
        }
    }

    [Fact]
    public async Task Failed_delivery_is_retried_with_new_delivery_id_and_same_event_id()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target) = await VerifiedTargetAsync(h);
        h.Faults.FailNextDeliveries = 1;
        var report = await RunWebhookScenarioAsync(h, user, project, target, "duplicate-webhook-v1");
        var deliveries = report.GetProperty("deliveries").EnumerateArray().ToList();
        Assert.Contains(deliveries, d => d.GetProperty("attemptNumber").GetInt32() == 2);
        Assert.Single(deliveries.Select(d => d.GetProperty("eventId").GetString()).Distinct());
        Assert.Equal(deliveries.Count, deliveries.Select(d => d.GetProperty("deliveryId").GetString()).Distinct().Count());
        Assert.Equal(("Passed", "AdapterReported"), Checks(report)["processed-exactly-once"]);
    }

    /// <summary>
    /// Crash fixture: the worker sent a delivery (the receiver got it) but died before recording the result. The DB is
    /// left exactly as at that moment (SentAt set, outcome Pending, job leased by a dead worker with an expired lease).
    /// Recovery must resend with the SAME delivery id and the receiver must not apply the payment twice.
    /// </summary>
    [Fact]
    public async Task Crash_after_send_before_record_is_recovered_at_least_once_without_double_apply()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target) = await VerifiedTargetAsync(h);
        var created = await user.PostAsync($"/api/projects/{project}/runs", new { scenario = "out-of-order-webhook-v1", webhookTargetId = target });
        var run = (await TestUser.JsonAsync(created)).GetProperty("id").GetGuid();
        await Arrange.StartAsync(user, run);

        WebhookDeliveryAttempt first;
        await using (var ctx = db.CreateContext())
        {
            first = await ctx.WebhookDeliveryAttempts.Where(d => d.RunId == run).OrderBy(d => d.PlannedOrder).FirstAsync(Ct);
            var now = h.Api.Clock.GetUtcNow();
            // The send happened (receiver saw it) …
            var ts = now.ToUnixTimeSeconds();
            var secret = APIArena.Domain.Outbound.WebhookSignature.SecretBytes(h.ReceiverOptions.SigningSecret);
            var request = new HttpRequestMessage(HttpMethod.Post, h.ReceiverUrl) { Content = new StringContent(first.PayloadJson, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Arena-Timestamp", ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-Arena-Delivery-Id", first.Id.ToString());
            request.Headers.Add("X-Arena-Event-Id", first.EventId);
            request.Headers.Add("X-Arena-Run-Id", run.ToString());
            request.Headers.Add("X-Arena-Signature", APIArena.Domain.Outbound.WebhookSignature.Compute(secret, ts, first.Id.ToString(), first.PayloadJson));
            Assert.Equal(HttpStatusCode.OK, (await new HttpClient().SendAsync(request, Ct)).StatusCode);

            // … but the worker died before recording it: SentAt set, still Pending, job leased by a dead worker.
            await ctx.WebhookDeliveryAttempts.Where(d => d.Id == first.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.SentAt, now), Ct);
            await ctx.OutboxJobs.Where(j => j.RunId == run && j.PayloadJson.Contains(first.Id.ToString()))
                .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, OutboxStatus.Leased).SetProperty(j => j.LeaseOwner, "crashed-worker")
                    .SetProperty(j => j.LeaseUntil, now.AddSeconds(-1)).SetProperty(j => j.Attempts, 1), Ct);
        }

        h.Api.Clock.Advance(TimeSpan.FromSeconds(3));
        await h.DrainAsync();

        await using var check = db.CreateContext();
        var recovered = await check.WebhookDeliveryAttempts.SingleAsync(d => d.Id == first.Id, Ct);
        Assert.Equal(DeliveryAttemptOutcome.Delivered, recovered.Outcome); // resent with the same delivery id, acknowledged
        Assert.Equal(1, await check.WebhookDeliveryAttempts.CountAsync(d => d.RunId == run && d.PlannedOrder == first.PlannedOrder, Ct)); // no new attempt id
        var rows = await h.Store.AggregatesAsync(run, [first.AggregateId], Ct);
        Assert.Equal(first.EventVersion, rows.Single().Version);
        Assert.Equal(1, await h.Store.AppliedCountAsync(run, first.EventId, first.AggregateId, Ct)); // applied once
    }

    [Fact]
    public async Task Cancelling_a_run_cancels_pending_deliveries()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        var (user, project, target) = await VerifiedTargetAsync(h);
        var created = await user.PostAsync($"/api/projects/{project}/runs", new { scenario = "out-of-order-webhook-v1", webhookTargetId = target });
        var run = (await TestUser.JsonAsync(created)).GetProperty("id").GetGuid();
        await Arrange.StartAsync(user, run);
        await user.PostAsync($"/api/runs/{run}/cancel");
        h.Api.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.DrainAsync();

        var deliveries = await user.GetJsonAsync($"/api/runs/{run}/deliveries");
        Assert.All(deliveries.EnumerateArray(), d => Assert.Equal("Cancelled", d.GetProperty("outcome").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await (await TestUser.CreateVerifiedAsync(h.Api)).GetAsync($"/api/runs/{run}/deliveries")).StatusCode);
    }

    [Fact]
    public async Task Outbox_claims_are_exclusive_across_workers_and_expired_leases_are_recovered()
    {
        var seen = new ConcurrentBag<Guid>();
        await using var h = await WebhookHarness.CreateAsync(db, extraWorkerServices: s => s.AddScoped<IJobHandler>(_ => new CountingHandler(seen)));
        var now = h.Api.Clock.GetUtcNow();
        var ids = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();
        await using (var ctx = db.CreateContext())
        {
            foreach (var id in ids)
            {
                ctx.OutboxJobs.Add(new OutboxJob { Id = id, Type = CountingHandler.JobType, PayloadJson = "{}", Status = OutboxStatus.Pending, MaxAttempts = 1, NextAttemptAt = now, CreatedAt = now, UpdatedAt = now });
            }

            // A job leased by a crashed worker (lease already expired) must be picked up again.
            var orphan = Guid.NewGuid();
            ids.Add(orphan);
            ctx.OutboxJobs.Add(new OutboxJob { Id = orphan, Type = CountingHandler.JobType, PayloadJson = "{}", Status = OutboxStatus.Leased, LeaseOwner = "dead-worker",
                LeaseUntil = now.AddSeconds(-1), Attempts = 1, MaxAttempts = 3, NextAttemptAt = now, CreatedAt = now, UpdatedAt = now });
            await ctx.SaveChangesAsync(Ct);
        }

        var workers = Enumerable.Range(0, 4).Select(_ => h.NewProcessor()).ToList();
        for (var round = 0; round < 5; round++)
        {
            await Task.WhenAll(workers.Select(w => w.RunOnceAsync(7, Ct)));
        }

        Assert.Equal(ids.Count, seen.Count);
        Assert.Equal(ids.OrderBy(x => x), seen.OrderBy(x => x));
        await using var check = db.CreateContext();
        Assert.Equal(ids.Count, await check.OutboxJobs.CountAsync(j => ids.Contains(j.Id) && j.Status == OutboxStatus.Succeeded, Ct));
    }

    private sealed class CountingHandler(ConcurrentBag<Guid> seen) : IJobHandler
    {
        public const string JobType = "test-count";
        public string Type => JobType;

        public Task HandleAsync(OutboxJob job, CancellationToken ct)
        {
            seen.Add(job.Id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Sample_receiver_rejects_forgeries_and_applies_parallel_duplicates_once()
    {
        await using var h = await WebhookHarness.CreateAsync(db);
        h.ReceiverOptions.SigningSecret = "whsec_test-receiver-secret";
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{h.Receiver.Port}") };
        var runId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            id = "evt_parallel_" + runId.ToString("N")[..8], type = "payment.succeeded", runId, aggregateId = "pay_parallel_" + runId.ToString("N")[..8], version = 1,
            data = new { paymentId = "x", status = "succeeded", amount = 4200, currency = "TRY" }, synthetic = true,
        });
        var now = h.Api.Clock.GetUtcNow().ToUnixTimeSeconds();

        HttpRequestMessage Signed(string deliveryId, long ts, string secret, Guid run, string payload)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/webhooks/apiarena") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            var sig = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{deliveryId}.{payload}")));
            r.Headers.Add("X-Arena-Signature", sig);
            r.Headers.Add("X-Arena-Timestamp", ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Headers.Add("X-Arena-Delivery-Id", deliveryId);
            r.Headers.Add("X-Arena-Event-Id", "evt");
            r.Headers.Add("X-Arena-Run-Id", run.ToString());
            return r;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed("d0", now, "whsec_attacker", runId, body), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed("d0", now - 3600, "whsec_test-receiver-secret", runId, body), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Signed("d0", now, "whsec_test-receiver-secret", Guid.NewGuid(), body), Ct)).StatusCode);

        var responses = await Task.WhenAll(Enumerable.Range(1, 10).Select(i => http.SendAsync(Signed("d" + i, now, "whsec_test-receiver-secret", runId, body), Ct)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var statuses = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>(Ct)));
        Assert.Single(statuses, s => s.GetProperty("status").GetString() == "Applied");

        // Replay of an already seen delivery id is acknowledged but not re-applied.
        var replay = await http.SendAsync(Signed("d1", now, "whsec_test-receiver-secret", runId, body), Ct);
        Assert.Equal("duplicate-delivery-acknowledged", (await replay.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString());

        var rows = await h.Store.AggregatesAsync(runId, ["pay_parallel_" + runId.ToString("N")[..8]], Ct);
        Assert.Equal(1, rows.Single().UpdateCount);
        Assert.Equal(4200, rows.Single().Amount);
    }
}
