using System.Net;
using System.Text.Json;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace APIArena.IntegrationTests;

public sealed class Stage01_FoundationTests(SqlDatabaseFixture db)
{
    [Fact]
    public async Task Migrations_are_applied_to_empty_test_database()
    {
        await using var ctx = db.CreateContext();
        var applied = await ctx.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        var pending = await ctx.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(applied);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Liveness_and_readiness_are_healthy_with_database()
    {
        await using var api = new ApiFactory(db);
        var client = api.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);
        var ready = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var summary = await client.GetAsync("/api/health", ct);
        var json = JsonDocument.Parse(await summary.Content.ReadAsStringAsync(ct));
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("control", json.RootElement.GetProperty("plane").GetString());
    }

    [Fact]
    public async Task Liveness_is_independent_of_database_but_readiness_is_not()
    {
        await using var api = new ApiFactory(db);
        api.Overrides["ConnectionStrings:ApiArena"] = "Server=127.0.0.1,1;Database=Nope;Integrated Security=true;Connect Timeout=1;Encrypt=false";
        var client = api.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/health", ct)).StatusCode);
    }

    [Fact]
    public async Task Simulator_host_has_its_own_health_endpoints()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var client = sim.CreateClient();
        var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("\"plane\":\"simulator\"", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Anonymous_unknown_route_is_401_problem_details_with_correlation_id_and_security_headers()
    {
        await using var api = new ApiFactory(db);
        var client = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/does-not-exist");
        request.Headers.Add("X-Correlation-ID", "test-correlation-1234");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Fallback authorization policy: anonymous callers cannot probe which routes exist.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("test-correlation-1234", body.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("test-correlation-1234", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Malformed_correlation_id_is_replaced_not_echoed()
    {
        await using var api = new ApiFactory(db);
        var client = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", "<script>alert(1)</script>");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var id = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.DoesNotContain("<", id);
        Assert.Matches("^[a-f0-9]{32}$", id);
    }
}
