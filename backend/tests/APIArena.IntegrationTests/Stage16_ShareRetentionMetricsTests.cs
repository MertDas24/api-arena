using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.RegularExpressions;
using APIArena.Application.Ops;
using APIArena.Domain.Runs;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.IntegrationTests;

public sealed partial class Stage16_ShareRetentionMetricsTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("/share/(aa_sh_[A-Za-z0-9_-]{43})$")]
    private static partial Regex ShareUrl();

    private static async Task<(TestUser User, Guid Project, Guid Run, string Token)> SharedRunAsync(ApiFactory api, SimulatorFactory sim)
    {
        var (user, project, run, key) = await Arrange.RunningAsync(api);
        await sim.CreateClient().SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "secret-order-key-123", "{\"password\":\"x\"}"));
        await user.PostAsync($"/api/runs/{run}/finish");
        var created = await user.PostAsync($"/api/runs/{run}/shares", new { expiresInDays = 7 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var url = (await TestUser.JsonAsync(created)).GetProperty("url").GetString()!;
        return (user, project, run, ShareUrl().Match(url).Groups[1].Value);
    }

    [Fact]
    public async Task Public_share_is_sanitised_noindex_and_token_is_hashed()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, run, token) = await SharedRunAsync(api, sim);
        Assert.False(string.IsNullOrEmpty(token));

        var anon = api.CreateClient(new() { HandleCookies = false });
        var response = await anon.GetAsync($"/api/public/shares/{token}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("noindex", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("secret-order-key-123", body);
        Assert.DoesNotContain(user.Email, body);
        Assert.DoesNotContain("Demo proje", body); // project name
        Assert.DoesNotContain(run.ToString(), body);
        Assert.DoesNotContain("password", body);
        Assert.Contains("\"checks\"", body);

        await using var ctx = db.CreateContext();
        Assert.DoesNotContain(await ctx.ShareLinks.Select(s => s.DisplayPrefix).ToListAsync(Ct), p => p == token);
        Assert.True(await ctx.ShareLinks.AnyAsync(s => s.TokenHash == OpaqueTokens.Hash(token), Ct));
    }

    [Fact]
    public async Task Shares_require_a_report_are_owner_scoped_and_stop_working_on_revoke_expiry_and_deletion()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, project, run, token) = await SharedRunAsync(api, sim);
        var anon = api.CreateClient(new() { HandleCookies = false });

        // Not finished → no share.
        var (u2, _, running, _) = await Arrange.RunningAsync(api);
        Assert.Equal(HttpStatusCode.Conflict, (await u2.PostAsync($"/api/runs/{running}/shares", new { })).StatusCode);

        // Other user cannot list/create/revoke.
        var shares = await user.GetJsonAsync($"/api/runs/{run}/shares");
        var shareId = shares[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await u2.GetAsync($"/api/runs/{run}/shares")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u2.PostAsync($"/api/runs/{run}/shares", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u2.SendAsync(HttpMethod.Delete, $"/api/runs/{run}/shares/{shareId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync($"/api/runs/{run}/shares", new { expiresInDays = 365 })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/public/shares/aa_sh_" + new string('A', 43), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/public/shares/nonsense", Ct)).StatusCode);

        // Revoke.
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Delete, $"/api/runs/{run}/shares/{shareId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/shares/{token}", Ct)).StatusCode);

        // Expiry.
        var second = await user.PostAsync($"/api/runs/{run}/shares", new { expiresInDays = 1 });
        var token2 = ShareUrl().Match((await TestUser.JsonAsync(second)).GetProperty("url").GetString()!).Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/api/public/shares/{token2}", Ct)).StatusCode);
        api.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/shares/{token2}", Ct)).StatusCode);

        // Project deletion revokes (the 1-day clock jump also ended the 8 h session).
        await user.LoginAsync();
        var third = await user.PostAsync($"/api/runs/{run}/shares", new { expiresInDays = 7 });
        var token3 = ShareUrl().Match((await TestUser.JsonAsync(third)).GetProperty("url").GetString()!).Groups[1].Value;
        await user.SendAsync(HttpMethod.Delete, $"/api/projects/{project}");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/public/shares/{token3}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Account_deletion_kills_shares()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, _, _, token) = await SharedRunAsync(api, sim);
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Delete, "/api/account", new { password = user.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync($"/api/public/shares/{token}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Retention_is_bounded_idempotent_and_keeps_reports_until_their_own_deadline()
    {
        // Early fake clock: this test's retention passes cannot touch data of tests running in parallel (2026 dates).
        await using var api = new ApiFactory(db, new DateTimeOffset(2019, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, project, run, key) = await Arrange.RunningAsync(api);
        await sim.CreateClient().SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "op", "{}"));
        await user.PostAsync($"/api/runs/{run}/finish");
        var deletedProject = await Arrange.ProjectAsync(user, "silinecek");
        await user.SendAsync(HttpMethod.Delete, $"/api/projects/{deletedProject}");

        async Task<RetentionResult> PassAsync()
        {
            using var scope = api.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<RetentionService>().CleanupAsync(Ct);
        }

        api.Clock.Advance(TimeSpan.FromDays(8));
        var first = await PassAsync();
        Assert.True(first.Observations >= 1);
        Assert.True(first.Projects >= 1);
        Assert.True(first.Keys >= 1);
        await using (var ctx = db.CreateContext())
        {
            Assert.False(await ctx.RequestObservations.AnyAsync(o => o.RunId == run, Ct));
            Assert.False(await ctx.Projects.AnyAsync(p => p.Id == deletedProject, Ct));
            Assert.True(await ctx.ReportSnapshots.AnyAsync(r => r.RunId == run, Ct));
        }

        await user.LoginAsync(); // session idle timeout passed with the clock jump
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/api/runs/{run}/report")).StatusCode);
        Assert.Equal(0, (await PassAsync()).Observations); // idempotent re-run

        api.Clock.Advance(TimeSpan.FromDays(23));
        var later = await PassAsync();
        await user.LoginAsync();
        Assert.True(later.Runs >= 1);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/runs/{run}/report")).StatusCode);
        Assert.True(await (await Task.FromResult(db.CreateContext())).Projects.AnyAsync(p => p.Id == project, Ct)); // live project kept
    }

    [Fact]
    public async Task Metrics_use_only_low_cardinality_tags()
    {
        var measurements = new ConcurrentBag<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ArenaMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => measurements.Add((i.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((i, _, tags, _) => measurements.Add((i.Name, tags.ToArray())));
        listener.Start();

        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (_, _, run, key) = await Arrange.RunningAsync(api);
        var c = sim.CreateClient();
        await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "op", "{}"));
        await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "wrong/route", key));
        await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", null));

        var sims = measurements.Where(m => m.Name == "apiarena.simulator.requests").ToList();
        Assert.Contains(sims, m => m.Tags.Any(t => t.Value as string == "scenario"));
        Assert.Contains(sims, m => m.Tags.Any(t => t.Value as string == "rejected"));
        Assert.Contains(sims, m => m.Tags.Any(t => t.Value as string == "platform"));
        Assert.Contains(measurements, m => m.Name == "apiarena.simulator.plan_ms");

        var guid = new Regex("[0-9a-f]{8}-[0-9a-f]{4}", RegexOptions.IgnoreCase);
        Assert.All(measurements.SelectMany(m => m.Tags), t =>
        {
            Assert.Contains(t.Key, new[] { "result", "kind", "reason", "outcome", "type" });
            Assert.DoesNotMatch(guid, t.Value?.ToString() ?? "");
            Assert.DoesNotContain(key, t.Value?.ToString() ?? "");
        });
    }
}
