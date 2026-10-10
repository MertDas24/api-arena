using System.Net;
using System.Text.Json;
using APIArena.IntegrationTests.Infrastructure;

namespace APIArena.IntegrationTests;

public sealed class Stage06_FeedTests(SqlDatabaseFixture db)
{
    private static async Task<(ApiFactory Api, SimulatorFactory Sim, TestUser User, Guid Run, string Key)> WithTrafficAsync(SqlDatabaseFixture db, int requests)
    {
        var api = new ApiFactory(db);
        var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();
        for (var i = 0; i < requests; i++)
        {
            await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "feed-op", "{}"));
        }

        return (api, sim, user, run, key);
    }

    [Fact]
    public async Task Feed_returns_ordered_append_only_events_with_observation_summaries()
    {
        var (api, sim, user, run, key) = await WithTrafficAsync(db, 3);
        await using var _ = api;
        await using var __ = sim;

        var feed = await user.GetJsonAsync($"/api/runs/{run}/events?afterSequence=0");
        var items = feed.GetProperty("items").EnumerateArray().ToList();
        var sequences = items.Select(i => i.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(sequences.OrderBy(s => s), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());

        var types = items.Select(i => i.GetProperty("type").GetString()).ToList();
        Assert.Equal(["RunCreated", "KeyIssued", "RunStarted"], types.Take(3));
        Assert.Equal(3, types.Count(t => t == "RequestReceived"));
        Assert.Equal(3, types.Count(t => t == "ResponseOutcome"));

        var received = items.First(i => i.GetProperty("type").GetString() == "RequestReceived");
        var summary = received.GetProperty("observation");
        Assert.Equal(503, summary.GetProperty("plannedStatus").GetInt32());
        Assert.True(summary.GetProperty("isSimulatedError").GetBoolean());
        Assert.Equal("feed-op", summary.GetProperty("operationKey").GetString());
        Assert.DoesNotContain(key, feed.ToString());
    }

    [Fact]
    public async Task Reconnecting_with_after_sequence_neither_loses_nor_duplicates()
    {
        var (api, sim, user, run, key) = await WithTrafficAsync(db, 2);
        await using var _ = api;
        await using var __ = sim;

        var full = (await user.GetJsonAsync($"/api/runs/{run}/events")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("sequence").GetInt64()).ToList();

        var collected = new List<long>();
        long after = 0;
        while (true)
        {
            var page = await user.GetJsonAsync($"/api/runs/{run}/events?afterSequence={after}&limit=2");
            var seqs = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sequence").GetInt64()).ToList();
            collected.AddRange(seqs);
            after = page.GetProperty("lastSequence").GetInt64();
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }

        Assert.Equal(full, collected);

        // New traffic after "disconnect" shows up exactly once on resume.
        await sim.CreateClient().SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "feed-op", "{}"));
        var resumed = await user.GetJsonAsync($"/api/runs/{run}/events?afterSequence={after}");
        var newSeqs = resumed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("sequence").GetInt64()).ToList();
        Assert.NotEmpty(newSeqs);
        Assert.All(newSeqs, s => Assert.True(s > after));
        Assert.Empty(newSeqs.Intersect(collected));
    }

    [Fact]
    public async Task Feed_page_size_is_bounded()
    {
        var (api, sim, user, run, _) = await WithTrafficAsync(db, 1);
        await using var _ = api;
        await using var __ = sim;
        var page = await user.GetJsonAsync($"/api/runs/{run}/events?limit=100000");
        Assert.True(page.GetProperty("items").GetArrayLength() <= 200);
    }

    [Fact]
    public async Task Observation_list_filter_and_detail_are_masked()
    {
        var (api, sim, user, run, key) = await WithTrafficAsync(db, 3);
        await using var _ = api;
        await using var __ = sim;

        var errors = await user.GetJsonAsync($"/api/runs/{run}/observations?status=503");
        Assert.Equal(2, errors.GetProperty("total").GetInt32());
        var id = errors.GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await user.GetJsonAsync($"/api/runs/{run}/observations/{id}");
        Assert.Equal("[REDACTED]", detail.GetProperty("headers").GetProperty("authorization").GetString());
        Assert.False(detail.GetProperty("bodyCaptureEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("bodyPreview").ValueKind);
        Assert.DoesNotContain(key, detail.ToString());
    }

    [Fact]
    public async Task Other_user_cannot_read_feed_observations_or_detail()
    {
        var (api, sim, user, run, _) = await WithTrafficAsync(db, 1);
        await using var _ = api;
        await using var __ = sim;
        var obsId = (await user.GetJsonAsync($"/api/runs/{run}/observations")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var other = await TestUser.CreateVerifiedAsync(api);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/runs/{run}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/runs/{run}/events?afterSequence=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/runs/{run}/observations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/runs/{run}/observations/{obsId}")).StatusCode);

        // A valid observation id under a run the attacker owns is still not readable.
        var otherProject = await Arrange.ProjectAsync(other);
        var otherRun = await Arrange.RunAsync(other, otherProject);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/runs/{otherRun}/observations/{obsId}")).StatusCode);

        var anon = TestUser.Anonymous(api);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/runs/{run}/events")).StatusCode);
    }
}
