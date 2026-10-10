using System.Net;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.IntegrationTests;

public sealed class Stage04_RunLifecycleTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Catalog_lists_three_versioned_builtins_with_teaching_content()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var list = await user.GetJsonAsync("/api/scenarios");
        var slugs = list.EnumerateArray().Select(s => s.GetProperty("slug").GetString()).ToList();
        Assert.Contains("retry-503-v1", slugs);
        Assert.Contains("rate-limit-v1", slugs);
        Assert.Contains("timeout-v1", slugs);
        foreach (var s in list.EnumerateArray().Where(s => s.GetProperty("kind").GetString() == "BuiltIn"))
        {
            var content = s.GetProperty("latest").GetProperty("content");
            foreach (var field in new[] { "description", "learningGoal", "setup", "expectedTrace", "evaluationRules", "unobservable", "limits", "curlExample", "connection" })
            {
                Assert.True(content.TryGetProperty(field, out _), $"{field} missing");
            }
        }
    }

    [Fact]
    public async Task Key_is_returned_once_and_only_its_hash_is_stored()
    {
        await using var api = new ApiFactory(db);
        var (user, _, runId, key) = await Arrange.RunningAsync(api);
        Assert.Matches("^aa_rk_[A-Za-z0-9_-]{43}$", key);

        var run = await user.GetJsonAsync($"/api/runs/{runId}");
        Assert.DoesNotContain(key, run.ToString());
        Assert.Equal(key[..10], run.GetProperty("keys")[0].GetProperty("prefix").GetString());

        await using var ctx = db.CreateContext();
        var stored = await ctx.RunAccessKeys.SingleAsync(k => k.RunId == runId, Ct);
        Assert.Equal(RunKeys.Hash(key), stored.KeyHash);
        var eventsJson = string.Join("\n", await ctx.RunEvents.Where(e => e.RunId == runId).Select(e => e.DataJson ?? "").ToListAsync(Ct));
        Assert.DoesNotContain(key, eventsJson);
        Assert.DoesNotContain(key, string.Join("\n", api.Logs.Lines));
    }

    [Fact]
    public async Task Concurrent_starts_start_the_run_exactly_once()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        var runId = await Arrange.RunAsync(user, project);
        await user.CsrfAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => user.PostAsync($"/api/runs/{runId}/start")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.RunEvents.CountAsync(e => e.RunId == runId && e.Type == "RunStarted", Ct));
        Assert.Equal(RunStatus.Running, (await ctx.Runs.SingleAsync(r => r.Id == runId, Ct)).Status);
    }

    [Fact]
    public async Task Finish_and_cancel_race_produces_one_consistent_final_state()
    {
        await using var api = new ApiFactory(db);
        var (user, _, runId, _) = await Arrange.RunningAsync(api);

        var calls = Enumerable.Range(0, 6).Select(i => user.PostAsync($"/api/runs/{runId}/{(i % 2 == 0 ? "finish" : "cancel")}")).ToArray();
        var responses = await Task.WhenAll(calls);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));

        await using var ctx = db.CreateContext();
        var run = await ctx.Runs.SingleAsync(r => r.Id == runId, Ct);
        Assert.Contains(run.Status, new[] { RunStatus.Completed, RunStatus.Cancelled });
        var terminalEvents = await ctx.RunEvents.CountAsync(e => e.RunId == runId && (e.Type == "RunCompleted" || e.Type == "RunCancelled"), Ct);
        Assert.Equal(1, terminalEvents);
        Assert.True(await ctx.RunAccessKeys.Where(k => k.RunId == runId).AllAsync(k => k.RevokedAt != null, Ct));
    }

    [Fact]
    public async Task Run_expires_after_ttl_lazily_and_by_sweep()
    {
        // Earlier clock: the global sweep below must not touch runs of tests running in parallel on the shared DB.
        await using var api = new ApiFactory(db, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (user, _, runId, _) = await Arrange.RunningAsync(api);
        var (user2, _, runId2, _) = await Arrange.RunningAsync(api);

        api.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var lazy = await user.GetJsonAsync($"/api/runs/{runId}");
        Assert.Equal("Expired", lazy.GetProperty("status").GetString());
        Assert.Equal("expired", lazy.GetProperty("incompleteReason").GetString());

        // Sweep path (what the worker calls, also after a restart): picks up the other overdue run.
        using (var scope = api.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<APIArena.Application.Runs.RunService>();
            Assert.True(await service.ExpireDueAsync(Ct) >= 1);
        }

        await using var ctx = db.CreateContext();
        Assert.Equal(RunStatus.Expired, (await ctx.Runs.SingleAsync(r => r.Id == runId2, Ct)).Status);
        var finish = await user2.PostAsync($"/api/runs/{runId2}/finish");
        Assert.Equal(HttpStatusCode.Conflict, finish.StatusCode);
    }

    [Fact]
    public async Task New_scenario_version_does_not_change_existing_run_snapshot()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);

        // Private template (isolated from other tests) cloned from the built-in timeout-v1 definition.
        Guid templateId;
        await using (var ctx = db.CreateContext())
        {
            var builtIn = await ctx.ScenarioVersions.FirstAsync(v => ctx.ScenarioTemplates.Any(t => t.Id == v.TemplateId && t.Slug == "timeout-v1" && t.OwnerId == null), Ct);
            var template = new ScenarioTemplate { Id = Guid.NewGuid(), Slug = "private-timeout", Kind = ScenarioKind.Custom, OwnerId = user.Id, Title = "Özel", Summary = "Özel", CreatedAt = DateTimeOffset.UtcNow };
            templateId = template.Id;
            ctx.ScenarioTemplates.Add(template);
            ctx.ScenarioVersions.Add(Clone(builtIn, template.Id, 1, builtIn.DefinitionJson, "timeout@1.0"));
            await ctx.SaveChangesAsync(Ct);
        }

        var runV1 = await Arrange.RunAsync(user, project, templateId.ToString());

        await using (var ctx = db.CreateContext())
        {
            var v1 = await ctx.ScenarioVersions.SingleAsync(v => v.TemplateId == templateId, Ct);
            ctx.ScenarioVersions.Add(Clone(v1, templateId, 2, v1.DefinitionJson.Replace("3000", "4000"), "timeout@2.0"));
            await ctx.SaveChangesAsync(Ct);
        }

        var old = await user.GetJsonAsync($"/api/runs/{runV1}");
        Assert.Equal(1, old.GetProperty("scenarioVersion").GetInt32());
        Assert.Equal("timeout@1.0", old.GetProperty("evaluatorVersion").GetString());
        Assert.Equal(3000, old.GetProperty("config").GetProperty("definition").GetProperty("delayMs").GetInt32());

        var runV2 = await Arrange.RunAsync(user, project, templateId.ToString());
        var fresh = await user.GetJsonAsync($"/api/runs/{runV2}");
        Assert.Equal(2, fresh.GetProperty("scenarioVersion").GetInt32());
        Assert.Equal(4000, fresh.GetProperty("config").GetProperty("definition").GetProperty("delayMs").GetInt32());

        var pinned = await user.PostAsync($"/api/projects/{project}/runs", new { scenario = templateId.ToString(), version = 1 });
        Assert.Equal(1, (await TestUser.JsonAsync(pinned)).GetProperty("scenarioVersion").GetInt32());

        // Another user cannot see or use the private template.
        var other = await TestUser.CreateVerifiedAsync(api);
        var otherProject = await Arrange.ProjectAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/projects/{otherProject}/runs", new { scenario = templateId.ToString() })).StatusCode);
    }

    private static ScenarioVersion Clone(ScenarioVersion from, Guid templateId, int version, string definition, string evaluator) => new()
    {
        Id = Guid.NewGuid(),
        TemplateId = templateId,
        Version = version,
        Engine = from.Engine,
        EvaluatorVersion = evaluator,
        DefinitionJson = definition,
        ContentJson = from.ContentJson,
        ContentHash = "test-" + version,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    [Fact]
    public async Task Rotating_a_key_revokes_the_previous_one()
    {
        await using var api = new ApiFactory(db);
        var (user, _, runId, first) = await Arrange.RunningAsync(api);
        var second = await Arrange.KeyAsync(user, runId);
        Assert.NotEqual(first, second);

        await using var ctx = db.CreateContext();
        var keys = await ctx.RunAccessKeys.Where(k => k.RunId == runId).ToListAsync(Ct);
        Assert.Single(keys, k => k.RevokedAt == null);
        Assert.NotNull(keys.Single(k => k.KeyHash.SequenceEqual(RunKeys.Hash(first))).RevokedAt);
    }

    [Fact]
    public async Task Active_run_quota_holds_under_parallel_starts()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        var runs = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            runs.Add(await Arrange.RunAsync(user, project));
        }

        await user.CsrfAsync();
        var responses = await Task.WhenAll(runs.Select(r => user.PostAsync($"/api/runs/{r}/start")));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        Assert.Equal(2, rejected.Count);
        Assert.Equal("quota_active_runs", await TestUser.ProblemCodeAsync(rejected[0]));

        await using var ctx = db.CreateContext();
        Assert.Equal(3, await ctx.Runs.CountAsync(r => r.ProjectId == project && r.Status == RunStatus.Running, Ct));
    }

    [Fact]
    public async Task Daily_quota_is_enforced()
    {
        await using var api = new ApiFactory(db);
        api.Overrides["ApiArena:Limits:DailyRunsPerUser"] = "2";
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        for (var i = 0; i < 2; i++)
        {
            var r = await Arrange.RunAsync(user, project);
            await Arrange.StartAsync(user, r);
            await user.PostAsync($"/api/runs/{r}/cancel");
        }

        var third = await Arrange.RunAsync(user, project);
        var response = await user.PostAsync($"/api/runs/{third}/start");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("quota_daily_runs", await TestUser.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Unverified_account_cannot_start_runs()
    {
        await using var api = new ApiFactory(db);
        var user = TestUser.Anonymous(api);
        await user.PostAsync("/api/auth/register", new { email = user.Email, password = user.Password });
        await user.LoginAsync();
        var project = await Arrange.ProjectAsync(user);
        var run = await Arrange.RunAsync(user, project);
        var response = await user.PostAsync($"/api/runs/{run}/start");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("email_not_verified", await TestUser.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Cancel_created_run_revokes_keys_and_cannot_restart()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        var run = await Arrange.RunAsync(user, project);
        await Arrange.KeyAsync(user, run);
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/runs/{run}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync($"/api/runs/{run}/start")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostAsync($"/api/runs/{run}/access-keys")).StatusCode);
    }

    [Fact]
    public async Task Other_user_cannot_touch_run_or_create_runs_in_foreign_project()
    {
        await using var api = new ApiFactory(db);
        var (a, projectA, runA, _) = await Arrange.RunningAsync(api);
        var b = await TestUser.CreateVerifiedAsync(api);
        var keyId = (await a.GetJsonAsync($"/api/runs/{runA}")).GetProperty("keys")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/{runA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/projects/{projectA}/runs")).StatusCode);
        foreach (var action in new[] { "start", "finish", "cancel", "access-keys" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/runs/{runA}/{action}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(HttpMethod.Delete, $"/api/runs/{runA}/access-keys/{keyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/projects/{projectA}/runs", new { scenario = "retry-503-v1" })).StatusCode);

        var stillRunning = await a.GetJsonAsync($"/api/runs/{runA}");
        Assert.Equal("Running", stillRunning.GetProperty("status").GetString());
        Assert.True(stillRunning.GetProperty("keys")[0].GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Deleting_a_project_cancels_active_runs_and_revokes_keys()
    {
        await using var api = new ApiFactory(db);
        var (user, project, run, _) = await Arrange.RunningAsync(api);
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Delete, $"/api/projects/{project}")).StatusCode);

        await using var ctx = db.CreateContext();
        var r = await ctx.Runs.SingleAsync(x => x.Id == run, Ct);
        Assert.Equal(RunStatus.Cancelled, r.Status);
        Assert.Equal("project_deleted", r.IncompleteReason);
        Assert.True(await ctx.RunAccessKeys.Where(k => k.RunId == run).AllAsync(k => k.RevokedAt != null, Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/runs/{run}")).StatusCode);
    }
}
