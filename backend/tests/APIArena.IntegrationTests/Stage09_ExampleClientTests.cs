using System.Text.Json;
using ApiArena.ExampleClient;
using APIArena.IntegrationTests.Infrastructure;

namespace APIArena.IntegrationTests;

/// <summary>
/// The shipped example client is exercised against the real simulator + evaluator: the naive and the resilient mode
/// must produce measurably different reports on the same scenario (stage 09 acceptance).
/// </summary>
public sealed class Stage09_ExampleClientTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> RunScenarioAsync(SqlDatabaseFixture db, string scenario, Mode mode, bool kestrel = false)
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        if (kestrel)
        {
            sim.UseKestrel(0);
        }

        var (user, _, run, key) = await Arrange.RunningAsync(api, scenario + "-v1");
        var http = sim.CreateClient();
        http.BaseAddress = new Uri(http.BaseAddress!, $"/sim/runs/{run}/");
        http.Timeout = Timeout.InfiniteTimeSpan;
        var log = new List<string>();

        // Client-side waits advance the simulator's (fake) clock, so server receipt times reflect real backoff.
        var client = new ArenaClient(http, key, log.Add, (wait, _) =>
        {
            api.Clock.Advance(wait);
            return Task.CompletedTask;
        });

        var work = scenario switch
        {
            "retry-503" => client.ChargeAsync(mode, Ct),
            "rate-limit" => client.ListOrdersAsync(mode, Ct),
            _ => client.SlowReportAsync(mode, Ct),
        };

        // A naive timeout client waits for the server's 3 s delay, which runs on the fake clock.
        for (var i = 0; i < 400 && !work.IsCompleted; i++)
        {
            await Task.Delay(20, Ct);
            if (scenario == "timeout" && mode == Mode.Naive)
            {
                api.Clock.Advance(TimeSpan.FromMilliseconds(250));
            }
        }

        await work;
        Assert.DoesNotContain(key, string.Join("\n", log));
        var finish = await user.PostAsync($"/api/runs/{run}/finish");
        Assert.True(finish.IsSuccessStatusCode, $"finish → {(int)finish.StatusCode}: {await finish.Content.ReadAsStringAsync()} log: {string.Join(" | ", log)}");
        return await user.GetJsonAsync($"/api/runs/{run}/report");
    }

    private static Dictionary<string, string> Statuses(JsonElement report) =>
        report.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("status").GetString()!);

    [Fact]
    public async Task Retry503_naive_vs_resilient()
    {
        var naive = Statuses(await RunScenarioAsync(db, "retry-503", Mode.Naive));
        var resilient = Statuses(await RunScenarioAsync(db, "retry-503", Mode.Resilient));

        Assert.Equal("Failed", naive["idempotency-key-present"]);
        Assert.Equal("Failed", naive["retried-after-503"]);
        Assert.Equal("Failed", naive["reached-success"]);
        Assert.All(resilient.Values, s => Assert.Equal("Passed", s));
    }

    [Fact]
    public async Task RateLimit_naive_vs_resilient()
    {
        var naive = Statuses(await RunScenarioAsync(db, "rate-limit", Mode.Naive));
        var resilient = Statuses(await RunScenarioAsync(db, "rate-limit", Mode.Resilient));

        Assert.Equal("Failed", naive["respected-retry-after"]);
        Assert.Equal("Failed", naive["no-hammering"]);
        Assert.All(resilient.Values, s => Assert.Equal("Passed", s));
    }

    [Fact]
    public async Task Timeout_naive_vs_resilient()
    {
        var naive = Statuses(await RunScenarioAsync(db, "timeout", Mode.Naive, kestrel: true));
        var resilient = Statuses(await RunScenarioAsync(db, "timeout", Mode.Resilient, kestrel: true));

        Assert.Equal("Failed", naive["client-timeout-observed"]);
        Assert.Equal("Passed", resilient["client-timeout-observed"]);
        Assert.Equal("Passed", resilient["timeout-within-budget"]);
        Assert.Equal("Inconclusive", resilient["client-error-handling"]); // never inferable from HTTP traffic
    }
}
