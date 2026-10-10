using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using APIArena.IntegrationTests.Infrastructure;

namespace APIArena.IntegrationTests;

public sealed class Stage15_CustomScenarioTests(SqlDatabaseFixture db)
{
    private static object Definition(object[] steps, string route = "orders/create", string method = "POST") =>
        new { schemaVersion = 1, method, route, steps };

    private static readonly object[] ValidSteps =
    [
        new { status = 500, delayMs = 0, body = new { type = "json", content = "{\"error\":\"boom\"}" } },
        new { status = 429, delayMs = 0, retryAfterSeconds = 1, body = new { type = "json", content = "{\"error\":\"slow down\"}" } },
        new { status = 200, delayMs = 0, body = new { type = "text", content = "<script>alert(1)</script> ok" } },
    ];

    private static async Task<Guid> CreateAsync(TestUser user, object definition, string title = "Sipariş akışı")
    {
        var r = await user.PostAsync("/api/scenarios/custom", new { title, summary = "Özel test", definition });
        Assert.True(r.StatusCode == HttpStatusCode.Created, await r.Content.ReadAsStringAsync());
        return (await TestUser.JsonAsync(r)).GetProperty("id").GetGuid();
    }

    public static IEnumerable<TheoryDataRow<string, string>> InvalidDefinitions() =>
    [
        new("{\"schemaVersion\":1,\"method\":\"POST\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0}],\"script\":\"process.exit()\"}", "unknown field"),
        new("{\"schemaVersion\":1,\"method\":\"POST\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0,\"headers\":{\"Set-Cookie\":\"a=b\"}}]}", "arbitrary headers"),
        new("{\"schemaVersion\":2,\"method\":\"POST\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0}]}", "schema version"),
        new("{\"schemaVersion\":1,\"method\":\"TRACE\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0}]}", "method"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"../admin\",\"steps\":[{\"status\":200,\"delayMs\":0}]}", "path traversal"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"a/%2e%2e/b\",\"steps\":[{\"status\":200,\"delayMs\":0}]}", "encoded traversal"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[{\"status\":302,\"delayMs\":0}]}", "redirect status"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":5001}]}", "delay limit"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0,\"retryAfterSeconds\":5}]}", "retry-after on 200"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0,\"body\":{\"type\":\"json\",\"content\":\"{not json\"}}]}", "invalid json body"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0,\"body\":{\"type\":\"html\",\"content\":\"<b>x</b>\"}}]}", "html body"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[]}", "no steps"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[" + string.Join(",", Enumerable.Repeat("{\"status\":200,\"delayMs\":0}", 11)) + "]}", "too many steps"),
        new("{\"schemaVersion\":1,\"method\":\"GET\",\"route\":\"x\",\"steps\":[{\"status\":200,\"delayMs\":0,\"body\":{\"type\":\"text\",\"content\":\"" + new string('a', 4097) + "\"}}]}", "body too large"),
    ];

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public async Task Invalid_or_dangerous_definitions_are_rejected_by_the_server(string definitionJson, string reason)
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var r = await user.PostAsync("/api/scenarios/custom", new { title = "Kötü", definition = JsonDocument.Parse(definitionJson).RootElement });
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{reason}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        var preview = await user.PostAsync("/api/scenarios/custom/preview", new { definition = JsonDocument.Parse(definitionJson).RootElement });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
    }

    [Fact]
    public async Task Preview_and_real_run_have_identical_semantics()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        var user = await TestUser.CreateVerifiedAsync(api);
        var definition = Definition(ValidSteps);
        var preview = await TestUser.JsonAsync(await user.PostAsync("/api/scenarios/custom/preview", new { definition, attempts = 4 }));
        var expected = preview.EnumerateArray().Select(p => p.GetProperty("status").GetInt32()).ToList();
        Assert.Equal([500, 429, 200, 200], expected);

        var templateId = await CreateAsync(user, definition);
        var project = await Arrange.ProjectAsync(user);
        var run = await Arrange.RunAsync(user, project, templateId.ToString());
        var key = await Arrange.KeyAsync(user, run);
        await Arrange.StartAsync(user, run);

        var c = sim.CreateClient();
        var actual = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var r = await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Post, run, "orders/create", key, "custom-op", "{}"));
            actual.Add((int)r.StatusCode);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.Equal("1", r.Headers.GetValues("Retry-After").Single());
                api.Clock.Advance(TimeSpan.FromSeconds(1.2));
            }

            if (i == 2)
            {
                Assert.Equal("text/plain", r.Content.Headers.ContentType?.MediaType); // never text/html
                Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
                Assert.Contains("default-src 'none'", r.Headers.GetValues("Content-Security-Policy").Single());
            }
        }

        Assert.Equal(expected, actual);

        await user.PostAsync($"/api/runs/{run}/finish");
        var report = await user.GetJsonAsync($"/api/runs/{run}/report");
        var checks = report.GetProperty("checks").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("status").GetString());
        Assert.Equal("Passed", checks["all-steps-exercised"]);
        Assert.Equal("Passed", checks["retry-after-respected"]);
        Assert.Equal("Inconclusive", checks["business-outcome"]);
        Assert.Contains(report.GetProperty("unverifiable").EnumerateArray(), u => u.GetString()!.Contains("özel senaryolarda", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task New_version_leaves_old_runs_and_reports_unchanged()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var id = await CreateAsync(user, Definition(ValidSteps));
        var project = await Arrange.ProjectAsync(user);
        var oldRun = await Arrange.RunAsync(user, project, id.ToString());
        await user.PostAsync($"/api/runs/{oldRun}/cancel");
        var before = (await user.GetJsonAsync($"/api/runs/{oldRun}/report")).ToString();

        var v2 = await user.PostAsync($"/api/scenarios/custom/{id}/versions", new { definition = Definition([new { status = 503, delayMs = 0 }]) });
        Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
        Assert.Equal(2, (await TestUser.JsonAsync(v2)).GetProperty("latest").GetProperty("version").GetInt32());

        Assert.Equal(before, (await user.GetJsonAsync($"/api/runs/{oldRun}/report")).ToString());
        var oldRunDto = await user.GetJsonAsync($"/api/runs/{oldRun}");
        Assert.Equal(1, oldRunDto.GetProperty("scenarioVersion").GetInt32());
        var newRun = await Arrange.RunAsync(user, project, id.ToString());
        Assert.Equal(2, (await user.GetJsonAsync($"/api/runs/{newRun}")).GetProperty("scenarioVersion").GetInt32());
    }

    [Fact]
    public async Task Private_templates_are_invisible_to_other_users()
    {
        await using var api = new ApiFactory(db);
        var owner = await TestUser.CreateVerifiedAsync(api);
        var id = await CreateAsync(owner, Definition(ValidSteps), "Gizli <b>senaryo</b>");
        var other = await TestUser.CreateVerifiedAsync(api);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/scenarios/custom/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/scenarios/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/scenarios/custom/{id}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/scenarios/custom/{id}/versions", new { definition = Definition(ValidSteps) })).StatusCode);
        Assert.DoesNotContain(id.ToString(), (await other.GetJsonAsync("/api/scenarios")).ToString());
        var otherProject = await Arrange.ProjectAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/projects/{otherProject}/runs", new { scenario = id.ToString() })).StatusCode);

        // Title with markup is stored as data (rendered as text by the UI).
        Assert.Equal("Gizli <b>senaryo</b>", (await owner.GetJsonAsync($"/api/scenarios/custom/{id}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Import_rejects_oversized_unknown_and_malformed_documents_and_roundtrips_exports()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);

        async Task<HttpResponseMessage> Import(string json) =>
            await user.SendAsync(HttpMethod.Post, "/api/scenarios/custom/import", null, configure: r =>
            {
                r.Content = new StringContent(json, Encoding.UTF8);
                r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            });

        Assert.Equal(HttpStatusCode.BadRequest, (await Import("{\"format\":\"apiarena.custom-scenario\",\"title\":\"x\",\"definition\":{},\"pad\":\"" + new string('a', 40_000) + "\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("{\"format\":\"apiarena.custom-scenario\",\"title\":\"xyz\",\"definition\":{},\"$type\":\"System.IO.File\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("{\"format\":\"other\",\"title\":\"xyz\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import("not json")).StatusCode);

        var id = await CreateAsync(user, Definition(ValidSteps));
        var export = await user.GetAsync($"/api/scenarios/custom/{id}/export");
        Assert.Matches("^apiarena-custom-scenario-[0-9a-f]{8}\\.json$", export.Content.Headers.ContentDisposition?.FileNameStar ?? export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var reimported = await Import(await export.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, reimported.StatusCode);
    }
}
