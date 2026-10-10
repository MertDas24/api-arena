using System.Net;
using System.Text.Json;

namespace APIArena.IntegrationTests.Infrastructure;

/// <summary>Shared arrange helpers for run-level tests.</summary>
public static class Arrange
{
    public static async Task<Guid> ProjectAsync(TestUser user, string name = "Demo proje")
    {
        var response = await user.PostAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await TestUser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> RunAsync(TestUser user, Guid projectId, string scenario = "retry-503-v1")
    {
        var response = await user.PostAsync($"/api/projects/{projectId}/runs", new { scenario });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await TestUser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public static async Task<string> KeyAsync(TestUser user, Guid runId)
    {
        var response = await user.PostAsync($"/api/runs/{runId}/access-keys");
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await TestUser.JsonAsync(response)).GetProperty("key").GetString()!;
    }

    public static async Task<JsonElement> StartAsync(TestUser user, Guid runId)
    {
        var response = await user.PostAsync($"/api/runs/{runId}/start");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await TestUser.JsonAsync(response);
    }

    /// <summary>Verified user + project + run (+ key, started).</summary>
    public static async Task<(TestUser User, Guid ProjectId, Guid RunId, string Key)> RunningAsync(ApiFactory api, string scenario = "retry-503-v1")
    {
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await ProjectAsync(user);
        var run = await RunAsync(user, project, scenario);
        var key = await KeyAsync(user, run);
        await StartAsync(user, run);
        return (user, project, run, key);
    }
}
