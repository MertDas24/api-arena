using System.Net;
using APIArena.Domain.Entities;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace APIArena.IntegrationTests;

public sealed class Stage16_AccountDeletionTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Account_deletion_requires_password_and_removes_all_owned_data()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var (user, project, run, key) = await Arrange.RunningAsync(api);
        await sim.CreateClient().SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "op", "{}"));

        var wrong = await user.SendAsync(HttpMethod.Delete, "/api/account", new { password = "not-my-password" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var noCsrf = await user.SendAsync(HttpMethod.Delete, "/api/account", new { password = user.Password }, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);

        var ok = await user.SendAsync(HttpMethod.Delete, "/api/account", new { password = user.Password });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        await using var ctx = db.CreateContext();
        Assert.False(await ctx.Users.AnyAsync(u => u.Id == user.Id, Ct));
        Assert.False(await ctx.Projects.AnyAsync(p => p.Id == project, Ct));
        Assert.False(await ctx.Runs.AnyAsync(r => r.Id == run, Ct));
        Assert.False(await ctx.RequestObservations.AnyAsync(o => o.RunId == run, Ct));
        Assert.False(await ctx.UserSessions.AnyAsync(s => s.UserId == user.Id, Ct));

        // Key and session are dead.
        var simResponse = await sim.CreateClient().SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "payment/charge", key, "op", "{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, simResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
    }
}
