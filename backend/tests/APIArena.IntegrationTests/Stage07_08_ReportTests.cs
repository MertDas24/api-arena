using System.Net;
using System.Text;
using System.Text.Json;
using APIArena.Application.Reports;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.IntegrationTests;

public sealed class Stage07_08_ReportTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Charge(HttpClient c, Guid run, string key, string? op = "op") =>
        c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, op, "{}"));

    /// <summary>A well-behaved client: same key, 300 ms backoff on the shared fake clock, stops after 200.</summary>
    private static async Task GoodClientAsync(ApiFactory api, HttpClient sim, Guid run, string key)
    {
        for (var i = 0; i < 3; i++)
        {
            var r = await Charge(sim, run, key);
            if (r.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            api.Clock.Advance(TimeSpan.FromMilliseconds(300));
        }
    }

    [Fact]
    public async Task Good_client_report_has_evidence_for_every_pass()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, key) = await Arrange.RunningAsync(api);
        await GoodClientAsync(api, sim.CreateClient(), run, key);

        Assert.Equal(HttpStatusCode.Conflict, (await user.GetAsync($"/api/runs/{run}/report")).StatusCode); // not finished yet
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/runs/{run}/finish")).StatusCode);

        var report = await user.GetJsonAsync($"/api/runs/{run}/report");
        Assert.Equal(6, report.GetProperty("counts").GetProperty("passed").GetInt32());
        Assert.Equal(0, report.GetProperty("counts").GetProperty("failed").GetInt32());
        foreach (var check in report.GetProperty("checks").EnumerateArray())
        {
            Assert.Equal("Passed", check.GetProperty("status").GetString());
            Assert.True(check.GetProperty("evidence").GetArrayLength() > 0);
        }

        Assert.Equal("retry-503@1.0", report.GetProperty("evaluatorVersion").GetString());
        Assert.True(report.GetProperty("unverifiable").GetArrayLength() > 0);
        Assert.Equal(3, report.GetProperty("timeline").GetArrayLength());
    }

    [Fact]
    public async Task Bad_client_report_differs_from_good_client_and_compare_shows_improvement()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, project, bad, badKey) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();
        await Charge(c, bad, badKey, null); // no key, gives up after first 503
        await user.PostAsync($"/api/runs/{bad}/finish");

        var good = await Arrange.RunAsync(user, project);
        var goodKey = await Arrange.KeyAsync(user, good);
        await Arrange.StartAsync(user, good);
        await GoodClientAsync(api, c, good, goodKey);
        await user.PostAsync($"/api/runs/{good}/finish");

        var badReport = await user.GetJsonAsync($"/api/runs/{bad}/report");
        Assert.True(badReport.GetProperty("counts").GetProperty("failed").GetInt32() >= 3);

        var compare = await user.GetJsonAsync($"/api/runs/compare?left={bad}&right={good}");
        Assert.True(compare.GetProperty("comparable").GetBoolean());
        var changes = compare.GetProperty("checks").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("change").GetString());
        Assert.Equal("improved", changes["reached-success"]);
        Assert.Equal("improved", changes["idempotency-key-present"]);
    }

    [Fact]
    public async Task Compare_warns_on_different_scenarios_and_versions()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        var a = await Arrange.RunAsync(user, project, "retry-503-v1");
        var b = await Arrange.RunAsync(user, project, "rate-limit-v1");
        await user.PostAsync($"/api/runs/{a}/cancel");
        await user.PostAsync($"/api/runs/{b}/cancel");

        var compare = await user.GetJsonAsync($"/api/runs/compare?left={a}&right={b}");
        Assert.False(compare.GetProperty("comparable").GetBoolean());
        var warnings = string.Join("\n", compare.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Contains("Farklı senaryolar", warnings);
        Assert.Contains("Değerlendirici sürümleri farklı", warnings);
        Assert.Contains("eksik", warnings);
        Assert.Equal(0, compare.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Cancelled_empty_run_is_reported_honestly()
    {
        await using var api = new ApiFactory(db);
        var (user, _, run, _) = await Arrange.RunningAsync(api, "rate-limit-v1");
        await user.PostAsync($"/api/runs/{run}/cancel");
        var report = await user.GetJsonAsync($"/api/runs/{run}/report");
        Assert.Equal("Cancelled", report.GetProperty("runStatus").GetString());
        Assert.Equal("cancelled", report.GetProperty("incompleteReason").GetString());
        Assert.Equal(0, report.GetProperty("counts").GetProperty("passed").GetInt32());
        Assert.Equal(0, report.GetProperty("requestCount").GetInt32());
    }

    [Fact]
    public async Task Quota_limited_run_is_incomplete_not_failed()
    {
        await using var api = new ApiFactory(db);
        api.Overrides["ApiArena:Limits:RequestsPerRun"] = "2";
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();
        await Charge(c, run, key);
        api.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await Charge(c, run, key);
        api.Clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.Equal(HttpStatusCode.Conflict, (await Charge(c, run, key)).StatusCode);
        await user.PostAsync($"/api/runs/{run}/finish");

        var report = await user.GetJsonAsync($"/api/runs/{run}/report");
        Assert.Equal("quota_exceeded", report.GetProperty("incompleteReason").GetString());
        var success = report.GetProperty("checks").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "reached-success");
        Assert.Equal("Inconclusive", success.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Finalization_is_idempotent_and_report_is_immutable()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, key) = await Arrange.RunningAsync(api);
        await Charge(sim.CreateClient(), run, key);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => user.PostAsync($"/api/runs/{run}/finish")));
        var first = (await user.GetJsonAsync($"/api/runs/{run}/report")).ToString();

        using (var scope = api.Services.CreateScope())
        {
            var finalizer = scope.ServiceProvider.GetRequiredService<RunFinalizer>();
            await finalizer.FinalizeAsync(run, Ct);
            await finalizer.FinalizeAsync(run, Ct);
        }

        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.ReportSnapshots.CountAsync(s => s.RunId == run, Ct));
        Assert.Equal(first, (await user.GetJsonAsync($"/api/runs/{run}/report")).ToString());
    }

    [Fact]
    public async Task Broken_definition_produces_platform_failure_report_not_a_pass()
    {
        await using var api = new ApiFactory(db);
        var (user, _, run, _) = await Arrange.RunningAsync(api);
        await using (var ctx = db.CreateContext())
        {
            await ctx.Runs.Where(r => r.Id == run).ExecuteUpdateAsync(u => u.SetProperty(r => r.ConfigSnapshotJson, "{\"definition\":{\"unexpected\":true}}"), Ct);
        }

        await user.PostAsync($"/api/runs/{run}/finish");
        var report = await user.GetJsonAsync($"/api/runs/{run}/report");
        Assert.Equal("platform_failure", report.GetProperty("incompleteReason").GetString());
        Assert.Equal(0, report.GetProperty("counts").GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task Html_export_is_script_free_and_encodes_hostile_values()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();
        await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "%3Cscript%3Ealert(1)%3C%2Fscript%3E", key));
        await Charge(c, run, key);
        await user.PostAsync($"/api/runs/{run}/finish");

        var html = await user.GetAsync($"/api/runs/{run}/export?format=html");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", html.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches("^apiarena-report-[0-9a-f]{8}-\\d{8}\\.html$", html.Content.Headers.ContentDisposition?.FileNameStar ?? html.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var body = await html.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", body);
        Assert.Contains("default-src 'none'", body);
        Assert.DoesNotContain(key, body);

        var json = await user.GetAsync($"/api/runs/{run}/export?format=json");
        var text = await json.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, text);
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("masked").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await user.GetAsync($"/api/runs/{run}/export?format=../../etc/passwd")).StatusCode);
    }

    [Fact]
    public async Task Other_user_cannot_read_report_export_or_compare()
    {
        await using var api = new ApiFactory(db);
        var (a, project, runA, _) = await Arrange.RunningAsync(api);
        await a.PostAsync($"/api/runs/{runA}/finish");
        var runA2 = await Arrange.RunAsync(a, project);
        await a.PostAsync($"/api/runs/{runA2}/cancel");

        var b = await TestUser.CreateVerifiedAsync(api);
        var projectB = await Arrange.ProjectAsync(b);
        var runB = await Arrange.RunAsync(b, projectB);
        await b.PostAsync($"/api/runs/{runB}/cancel");

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/{runA}/report")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/{runA}/export?format=json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/{runA}/export?format=html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/compare?left={runA}&right={runA2}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/runs/compare?left={runB}&right={runA}")).StatusCode);
    }
}
