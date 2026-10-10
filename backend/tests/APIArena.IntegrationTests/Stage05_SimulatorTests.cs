using System.Net;
using System.Net.Http.Headers;
using System.Text;
using APIArena.Domain.Entities;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace APIArena.IntegrationTests;

public sealed class Stage05_SimulatorTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static HttpRequestMessage SimRequest(HttpMethod method, Guid runId, string route, string? key, string? opKey = null, string? body = null)
    {
        var r = new HttpRequestMessage(method, $"/sim/runs/{runId}/{route}");
        if (key is not null)
        {
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (opKey is not null)
        {
            r.Headers.Add("Idempotency-Key", opKey);
        }

        if (body is not null)
        {
            r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return r;
    }

    private static Task<HttpResponseMessage> Charge(HttpClient c, Guid run, string key, string op = "op-1") =>
        c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", key, op, "{\"amount\":100}"));

    private static async Task<List<RequestObservation>> Observations(SqlDatabaseFixture db, Guid runId)
    {
        await using var ctx = db.CreateContext();
        return await ctx.RequestObservations.Where(o => o.RunId == runId).OrderBy(o => o.Sequence).ToListAsync(Ct);
    }

    [Fact]
    public async Task Retry503_sequence_is_503_503_200_per_operation()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var r = await Charge(c, run, key);
            codes.Add(r.StatusCode);
            Assert.Equal("true", r.Headers.GetValues("X-Arena-Simulated").Single());
            Assert.False(r.Headers.Contains("X-Arena-Platform"));
        }

        Assert.Equal([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK], codes);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Charge(c, run, key, "op-2")).StatusCode); // separate operation starts over

        var obs = await Observations(db, run);
        Assert.Equal([1, 2, 3, 1], obs.Select(o => o.Attempt));
        Assert.All(obs, o => Assert.Equal(DeliveryOutcome.Sent, o.Outcome));
    }

    [Fact]
    public async Task Retry503_is_deterministic_under_parallel_requests()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Charge(c, run, key, "parallel-op")));
        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.ServiceUnavailable));
        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        var obs = await Observations(db, run);
        Assert.Equal(Enumerable.Range(1, 8), obs.Select(o => o.Attempt).OrderBy(a => a));
        Assert.Equal(obs.Count, obs.Select(o => o.Sequence).Distinct().Count());
        Assert.All(obs.Where(o => o.Attempt <= 2), o => Assert.Equal(503, o.PlannedStatus));
    }

    [Fact]
    public async Task RateLimit_honours_retry_after_window_on_server_clock()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api, "rate-limit-v1");
        var c = sim.CreateClient();

        var first = await c.SendAsync(SimRequest(HttpMethod.Get, run, "orders", key));
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal("2", first.Headers.GetValues("Retry-After").Single());
        Assert.False(first.Headers.Contains("X-Arena-Platform")); // expected scenario error, not a platform error

        api.Clock.Advance(TimeSpan.FromMilliseconds(1500));
        var early = await c.SendAsync(SimRequest(HttpMethod.Get, run, "orders", key));
        Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
        Assert.Equal("1", early.Headers.GetValues("Retry-After").Single());

        api.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(SimRequest(HttpMethod.Get, run, "orders", key))).StatusCode);
    }

    [Fact]
    public async Task RateLimit_window_opens_exactly_once_under_concurrency()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api, "rate-limit-v1");
        var c = sim.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => c.SendAsync(SimRequest(HttpMethod.Get, run, "orders", key))));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));

        await using var ctx = db.CreateContext();
        var stored = await ctx.Runs.Where(r => r.Id == run).Select(r => r.RateLimitedUntil).SingleAsync(Ct);
        Assert.Equal(api.Clock.GetUtcNow().AddSeconds(2), stored);
    }

    [Fact]
    public async Task Timeout_delay_completes_on_clock_and_disconnect_is_observed()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        sim.UseKestrel(0);
        var (_, _, run, key) = await Arrange.RunningAsync(api, "timeout-v1");
        var c = sim.CreateClient();

        // Client with a short budget: cancel after the request is planned → connection closed while the server waits.
        using var cts = new CancellationTokenSource();
        var pending = c.SendAsync(SimRequest(HttpMethod.Get, run, "reports/slow", key), cts.Token);
        await WaitUntil(async () => (await Observations(db, run)).Count == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitUntil(async () => (await Observations(db, run))[0].Outcome != DeliveryOutcome.Planned);
        Assert.Equal(DeliveryOutcome.ClientDisconnected, (await Observations(db, run))[0].Outcome);

        // Patient client: the delay finishes only when the (fake) clock reaches 3 s.
        var patient = c.SendAsync(SimRequest(HttpMethod.Get, run, "reports/slow", key), Ct);
        await WaitUntil(async () => (await Observations(db, run)).Count == 2);
        for (var i = 0; i < 40 && !patient.IsCompleted; i++)
        {
            api.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(25, Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await patient).StatusCode);
        var second = (await Observations(db, run))[1];
        await WaitUntil(async () => (await Observations(db, run))[1].Outcome == DeliveryOutcome.Sent);
        Assert.Equal(3000, second.PlannedDelayMs);
    }

    [Fact]
    public async Task Delay_is_released_when_run_is_cancelled()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, key) = await Arrange.RunningAsync(api, "timeout-v1");
        var c = sim.CreateClient();
        var pending = c.SendAsync(SimRequest(HttpMethod.Get, run, "reports/slow", key), Ct);
        await WaitUntil(async () => (await Observations(db, run)).Count == 1);
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/runs/{run}/cancel")).StatusCode);
        for (var i = 0; i < 20 && !pending.IsCompleted; i++)
        {
            api.Clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(25, Ct);
        }

        var response = await pending;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("run-ended-during-delay", response.Headers.GetValues("X-Arena-Platform").Single());
        await WaitUntil(async () => (await Observations(db, run))[0].Outcome == DeliveryOutcome.Rejected);
    }

    [Fact]
    public async Task Requests_without_valid_run_key_are_rejected_before_any_work()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, project, run, key) = await Arrange.RunningAsync(api);
        var other = await Arrange.RunAsync(user, project);
        var otherKey = await Arrange.KeyAsync(user, other);
        var c = sim.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", "not-a-key"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", "aa_rk_" + new string('A', 43)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", otherKey))).StatusCode); // key of another run

        // Control-panel session cookie is not a simulator credential.
        var login = await user.PostAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        var cookie = user.SessionCookieHeader(login)!;
        var withCookie = SimRequest(HttpMethod.Post, run, "payment/charge", null);
        withCookie.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(withCookie)).StatusCode);

        // Rotated key stops working; the new one works.
        user.ResetCsrf();
        var rotated = await Arrange.KeyAsync(user, run);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Charge(c, run, key)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Charge(c, run, rotated)).StatusCode);

        Assert.Single(await Observations(db, run)); // only the authenticated request was recorded
    }

    [Fact]
    public async Task Unknown_route_and_oversized_body_are_recorded_as_rejected()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();

        var wrong = await c.SendAsync(SimRequest(HttpMethod.Post, run, "admin/charge", key));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(SimRequest(HttpMethod.Get, run, "payment/charge", key))).StatusCode);

        var big = await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", key, "op", "{\"x\":\"" + new string('a', 70 * 1024) + "\"}"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);

        var longUrl = await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge?" + new string('q', 3000), key));
        Assert.Equal(HttpStatusCode.RequestUriTooLong, longUrl.StatusCode);

        var badOp = await c.SendAsync(SimRequest(HttpMethod.Post, run, "payment/charge", key, "bad key with spaces"));
        Assert.Equal(HttpStatusCode.BadRequest, badOp.StatusCode);

        var obs = await Observations(db, run);
        Assert.Contains(obs, o => o.RejectReason == "unknown_fixture_route");
        Assert.Contains(obs, o => o.RejectReason == "body_too_large" && o.Outcome == DeliveryOutcome.Rejected);
        Assert.Contains(obs, o => o.RejectReason == "invalid_idempotency_key");
        Assert.DoesNotContain(obs, o => o.Outcome == DeliveryOutcome.Sent);
    }

    [Fact]
    public async Task Oversized_headers_are_rejected_by_kestrel()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        sim.UseKestrel(0);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();
        var r = SimRequest(HttpMethod.Post, run, "payment/charge", key);
        r.Headers.Add("X-Padding", new string('h', 20 * 1024));
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, (await c.SendAsync(r, Ct)).StatusCode);
    }

    [Fact]
    public async Task Not_started_finished_and_expired_runs_reject_traffic_as_platform_errors()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var c = sim.CreateClient();
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);

        var created = await Arrange.RunAsync(user, project);
        var createdKey = await Arrange.KeyAsync(user, created);
        var notStarted = await Charge(c, created, createdKey);
        Assert.Equal(HttpStatusCode.Conflict, notStarted.StatusCode);
        Assert.Equal("run-not-running", notStarted.Headers.GetValues("X-Arena-Platform").Single());

        // Finishing through the API revokes the run's keys → the key no longer authenticates.
        var (finisher, _, finished, finishedKey) = await Arrange.RunningAsync(api);
        Assert.Equal(HttpStatusCode.OK, (await finisher.PostAsync($"/api/runs/{finished}/finish")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Charge(c, finished, finishedKey)).StatusCode);

        // Even if a key were still valid, a non-running run refuses traffic (defence in depth).
        var (_, _, stopped, stoppedKey) = await Arrange.RunningAsync(api);
        await using (var ctx = db.CreateContext())
        {
            await ctx.Runs.Where(r => r.Id == stopped).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RunStatus.Completed), Ct);
        }

        var stoppedResponse = await Charge(c, stopped, stoppedKey);
        Assert.Equal(HttpStatusCode.Conflict, stoppedResponse.StatusCode);
        Assert.Equal("run-not-running", stoppedResponse.Headers.GetValues("X-Arena-Platform").Single());

        var (_, _, expiring, expiringKey) = await Arrange.RunningAsync(api);
        api.Clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await Charge(c, expiring, expiringKey);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("run-expired", expired.Headers.GetValues("X-Arena-Platform").Single());
    }

    [Fact]
    public async Task Run_quota_is_enforced_and_marked_as_platform_limit()
    {
        await using var api = new ApiFactory(db);
        api.Overrides["ApiArena:Limits:RequestsPerRun"] = "3";
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Charge(c, run, key, "q-" + i)));
        Assert.Equal(3, responses.Count(r => r.Headers.Contains("X-Arena-Simulated")));
        var limited = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();
        Assert.Equal(3, limited.Count);
        Assert.All(limited, r => Assert.Equal("run-quota-exceeded", r.Headers.GetValues("X-Arena-Platform").Single()));

        await using var ctx = db.CreateContext();
        var stored = await ctx.Runs.SingleAsync(r => r.Id == run, Ct);
        Assert.Equal(3, stored.InboundCount);
        Assert.True(stored.QuotaExceeded);
    }

    [Fact]
    public async Task Attempt_counter_survives_simulator_restart()
    {
        await using var api = new ApiFactory(db);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        await using (var first = new SimulatorFactory(db, api.Clock))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Charge(first.CreateClient(), run, key)).StatusCode);
        }

        await using var second = new SimulatorFactory(db, api.Clock);
        var c = second.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Charge(c, run, key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Charge(c, run, key)).StatusCode);
    }

    [Fact]
    public async Task Cors_allows_only_listed_origins_without_credentials()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, project, run, key) = await Arrange.RunningAsync(api);
        await user.SendAsync(HttpMethod.Patch, $"/api/projects/{project}", new { allowedOrigins = new[] { "http://localhost:5173" } });
        var c = sim.CreateClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, $"/sim/runs/{run}/payment/charge");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var ok = await c.SendAsync(preflight);
        Assert.Equal("http://localhost:5173", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(ok.Headers.Contains("Access-Control-Allow-Credentials"));

        var evil = new HttpRequestMessage(HttpMethod.Options, $"/sim/runs/{run}/payment/charge");
        evil.Headers.Add("Origin", "https://evil.example");
        evil.Headers.Add("Access-Control-Request-Method", "POST");
        Assert.False((await c.SendAsync(evil)).Headers.Contains("Access-Control-Allow-Origin"));

        var actual = SimRequest(HttpMethod.Post, run, "payment/charge", key, "cors-op");
        actual.Headers.Add("Origin", "http://localhost:5173");
        var response = await c.SendAsync(actual);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Recorded_headers_and_bodies_are_masked()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        await user.SendAsync(HttpMethod.Patch, $"/api/projects/{project}", new { captureBodies = true });
        var run = await Arrange.RunAsync(user, project);
        var key = await Arrange.KeyAsync(user, run);
        await Arrange.StartAsync(user, run);

        var r = SimRequest(HttpMethod.Post, run, "payment/charge", key, "mask-op", "{\"amount\":5,\"cardNumber\":\"4111111111111111\",\"password\":\"hunter2!\",\"note\":\"<script>alert(1)</script>\"}");
        r.Headers.Add("Cookie", "session=supersecretcookie");
        r.Headers.Add("X-Api-Key", "sk_live_abcdef");
        await sim.CreateClient().SendAsync(r);

        var o = (await Observations(db, run)).Single();
        Assert.DoesNotContain(key, o.HeadersJson);
        Assert.DoesNotContain("supersecretcookie", o.HeadersJson);
        Assert.DoesNotContain("sk_live_abcdef", o.HeadersJson);
        Assert.Contains("[REDACTED]", o.HeadersJson);
        Assert.Contains("mask-op", o.HeadersJson);
        Assert.DoesNotContain("4111111111111111", o.BodyPreview);
        Assert.DoesNotContain("hunter2", o.BodyPreview);
        Assert.Contains("<script>", o.BodyPreview); // stored as data; rendered as plain text by the UI (stage 06 test)
        Assert.DoesNotContain(key, string.Join("\n", sim.Logs.Lines));
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        throw new TimeoutException("Condition not met within 5 s.");
    }
}
