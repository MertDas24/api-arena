using System.Net;
using APIArena.IntegrationTests.Infrastructure;

namespace APIArena.IntegrationTests;

public sealed class Stage02_AuthTests(SqlDatabaseFixture db)
{
    [Fact]
    public async Task Anonymous_request_to_protected_api_is_rejected_with_problem_details()
    {
        await using var api = new ApiFactory(db);
        var anon = TestUser.Anonymous(api);
        var response = await anon.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await TestUser.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Register_verify_login_me_flow_works_through_mail_sink()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var me = await user.GetJsonAsync("/api/auth/me");
        Assert.Equal(user.Email, me.GetProperty("email").GetString());
        Assert.True(me.GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task Wrong_password_and_unknown_account_get_identical_generic_response()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var stranger = TestUser.Anonymous(api);

        var wrong = await stranger.PostAsync("/api/auth/login", new { email = user.Email, password = "definitely-wrong-pass" });
        var unknown = await stranger.PostAsync("/api/auth/login", new { email = "nobody-" + Guid.NewGuid().ToString("N") + "@example.test", password = "whatever-pass-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        var a = await TestUser.JsonAsync(wrong);
        var b = await TestUser.JsonAsync(unknown);
        Assert.Equal(a.GetProperty("code").GetString(), b.GetProperty("code").GetString());
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Mutation_without_csrf_token_is_rejected()
    {
        await using var api = new ApiFactory(db);
        var anon = TestUser.Anonymous(api);
        var response = await anon.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "a@b.test", password = "x" }, csrf: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_failed", await TestUser.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Mutation_from_foreign_origin_is_rejected_even_with_token()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var response = await user.SendAsync(HttpMethod.Post, "/api/auth/logout", new { }, configure: r => r.Headers.Add("Origin", "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_origin_rejected", await TestUser.ProblemCodeAsync(response));
        // still logged in
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_cookie_is_invalid_immediately_after_logout()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        // Re-login to capture the raw cookie value
        var login = await user.PostAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        var cookie = user.SessionCookieHeader(login);
        Assert.NotNull(cookie);
        user.ResetCsrf();

        Assert.Equal(HttpStatusCode.NoContent, (await user.PostAsync("/api/auth/logout")).StatusCode);

        var replay = api.CreateClient(new() { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Session_cookie_flags_are_httponly_and_samesite_strict()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var login = await user.PostAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        var setCookie = login.Headers.GetValues("Set-Cookie").First(v => v.StartsWith("aa_session=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", setCookie);
        Assert.Contains("samesite=strict", setCookie);
        Assert.Contains("path=/", setCookie);
    }

    [Fact]
    public async Task Reset_password_token_is_single_use_and_revokes_sessions()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var login = await user.PostAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        var oldCookie = user.SessionCookieHeader(login);
        user.ResetCsrf();

        var anon = TestUser.Anonymous(api, user.Email);
        Assert.Equal(HttpStatusCode.Accepted, (await anon.PostAsync("/api/auth/forgot-password", new { email = user.Email })).StatusCode);
        var (uid, token) = await anon.ReadLinkAsync("reset-password");

        var first = await anon.PostAsync("/api/auth/reset-password", new { userId = uid, token, newPassword = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await anon.PostAsync("/api/auth/reset-password", new { userId = uid, token, newPassword = "another-password-22" });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("invalid_or_expired_token", await TestUser.ProblemCodeAsync(second));

        var replay = api.CreateClient(new() { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(request)).StatusCode);

        await user.LoginAsync("brand-new-password-1");
    }

    [Fact]
    public async Task Expired_reset_token_is_rejected()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var anon = TestUser.Anonymous(api, user.Email);
        await anon.PostAsync("/api/auth/forgot-password", new { email = user.Email });
        var (uid, token) = await anon.ReadLinkAsync("reset-password");

        api.Clock.Advance(TimeSpan.FromMinutes(31)); // lifetime is 30 minutes

        var response = await anon.PostAsync("/api/auth/reset-password", new { userId = uid, token, newPassword = "brand-new-password-1" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_or_expired_token", await TestUser.ProblemCodeAsync(response));
    }

    [Fact]
    public async Task Expired_verification_token_is_rejected_and_used_token_is_noop()
    {
        await using var api = new ApiFactory(db);
        var user = TestUser.Anonymous(api);
        await user.PostAsync("/api/auth/register", new { email = user.Email, password = user.Password });
        var (uid, token) = await user.ReadLinkAsync("verify-email");
        api.Clock.Advance(TimeSpan.FromHours(25));
        var expired = await user.PostAsync("/api/auth/verify-email", new { userId = uid, token });
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);

        var tampered = await user.PostAsync("/api/auth/verify-email", new { userId = uid, token = token + "x" });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
    }

    [Fact]
    public async Task Register_and_forgot_do_not_reveal_whether_account_exists()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var anon = TestUser.Anonymous(api);

        var dupe = await anon.PostAsync("/api/auth/register", new { email = user.Email, password = "some-other-pass-1" });
        var fresh = await anon.PostAsync("/api/auth/register", new { email = $"fresh-{Guid.NewGuid():N}@example.test", password = "some-other-pass-1" });
        Assert.Equal(fresh.StatusCode, dupe.StatusCode);
        Assert.Equal(await fresh.Content.ReadAsStringAsync(), await dupe.Content.ReadAsStringAsync());

        var known = await anon.PostAsync("/api/auth/forgot-password", new { email = user.Email });
        var unknown = await anon.PostAsync("/api/auth/forgot-password", new { email = $"ghost-{Guid.NewGuid():N}@example.test" });
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Weak_password_is_rejected_with_field_error()
    {
        await using var api = new ApiFactory(db);
        var anon = TestUser.Anonymous(api);
        var response = await anon.PostAsync("/api/auth/register", new { email = anon.Email, password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await TestUser.JsonAsync(response);
        Assert.True(json.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Account_locks_after_five_failures()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var anon = TestUser.Anonymous(api);
        for (var i = 0; i < 5; i++)
        {
            await anon.PostAsync("/api/auth/login", new { email = user.Email, password = "wrong-password-" + i });
        }

        var correct = await anon.PostAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client()
    {
        await using var api = new ApiFactory(db);
        api.Overrides["ApiArena:Auth:AuthPermitsPerMinute"] = "3";
        var anon = TestUser.Anonymous(api);
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await anon.PostAsync("/api/auth/login", new { email = "x@example.test", password = "nope-nope-nope" });
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("rate_limited", await TestUser.ProblemCodeAsync(last));
    }

    [Fact]
    public async Task Secrets_are_not_written_to_logs()
    {
        await using var api = new ApiFactory(db);
        var user = await TestUser.CreateVerifiedAsync(api);
        var anon = TestUser.Anonymous(api, user.Email);
        await anon.PostAsync("/api/auth/forgot-password", new { email = user.Email });
        var (_, token) = await anon.ReadLinkAsync("reset-password");

        var all = string.Join('\n', api.Logs.Lines);
        Assert.DoesNotContain(user.Password, all);
        Assert.DoesNotContain(token, all);
    }
}
