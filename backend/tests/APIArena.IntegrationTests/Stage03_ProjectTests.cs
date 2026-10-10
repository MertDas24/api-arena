using System.Net;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace APIArena.IntegrationTests;

public sealed class Stage03_ProjectTests(SqlDatabaseFixture db)
{
    public static async Task<Guid> CreateProjectAsync(TestUser user, string name = "Demo proje")
    {
        var response = await user.PostAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await TestUser.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Project_crud_roundtrip()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var id = await CreateProjectAsync(user, "İlk proje");

        var update = await user.SendAsync(HttpMethod.Patch, $"/api/projects/{id}", new
        {
            name = "Yeni ad",
            description = "Açıklama",
            captureBodies = true,
            allowedOrigins = new[] { "https://app.example.com", "http://localhost:5173" },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var dto = await user.GetJsonAsync($"/api/projects/{id}");
        Assert.Equal("Yeni ad", dto.GetProperty("name").GetString());
        Assert.True(dto.GetProperty("captureBodies").GetBoolean());
        Assert.Equal(2, dto.GetProperty("allowedOrigins").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(HttpMethod.Delete, $"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/projects/{id}")).StatusCode);
    }

    [Fact]
    public async Task User_B_cannot_read_update_or_delete_user_A_project()
    {
        await using var api = new ApiFactory(db);
        var a = await TestUser.CreateVerifiedAsync(api);
        var b = await TestUser.CreateVerifiedAsync(api);
        var id = await CreateProjectAsync(a, "A'nın projesi");

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/projects/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(HttpMethod.Patch, $"/api/projects/{id}", new { name = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(HttpMethod.Delete, $"/api/projects/{id}")).StatusCode);
        var listB = await b.GetJsonAsync("/api/projects");
        Assert.Equal(0, listB.GetProperty("total").GetInt32());

        var stillThere = await a.GetJsonAsync($"/api/projects/{id}");
        Assert.Equal("A'nın projesi", stillThere.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Mass_assignment_fields_are_ignored()
    {
        await using var api = new ApiFactory(db);
        var a = await TestUser.CreateVerifiedAsync(api);
        var b = await TestUser.CreateVerifiedAsync(api);
        var response = await a.PostAsync("/api/projects", new { name = "x", ownerId = b.Id, id = Guid.Empty, deletedAt = DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await TestUser.JsonAsync(response)).GetProperty("id").GetGuid();

        await using var ctx = db.CreateContext();
        var project = await ctx.Projects.SingleAsync(p => p.Id == id, TestContext.Current.CancellationToken);
        Assert.Equal(a.Id, project.OwnerId);
        Assert.Null(project.DeletedAt);
        Assert.NotEqual(Guid.Empty, project.Id);
    }

    [Fact]
    public async Task Listing_is_paged_sorted_by_allow_list_and_bounded()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        foreach (var n in new[] { "c", "a", "b" })
        {
            await CreateProjectAsync(user, n);
        }

        var page = await user.GetJsonAsync("/api/projects?sort=name&order=asc&page=1&pageSize=2");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal("a", page.GetProperty("items")[0].GetProperty("name").GetString());

        var big = await user.GetJsonAsync("/api/projects?pageSize=100000");
        Assert.Equal(50, big.GetProperty("pageSize").GetInt32());

        var badSort = await user.GetAsync("/api/projects?sort=OwnerId");
        Assert.Equal(HttpStatusCode.BadRequest, badSort.StatusCode);

        var search = await user.GetJsonAsync("/api/projects?search=%27%3B%20DROP%20TABLE%20Projects%3B--");
        Assert.Equal(0, search.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_project_name_is_rejected(string name)
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var response = await user.PostAsync("/api/projects", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await TestUser.JsonAsync(response)).GetProperty("errors").TryGetProperty("name", out _));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.example.com")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://user:pw@app.example.com")]
    [InlineData("ftp://app.example.com")]
    public async Task Wildcard_or_malformed_simulator_origins_are_rejected(string origin)
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var id = await CreateProjectAsync(user);
        var response = await user.SendAsync(HttpMethod.Patch, $"/api/projects/{id}", new { allowedOrigins = new[] { origin } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
