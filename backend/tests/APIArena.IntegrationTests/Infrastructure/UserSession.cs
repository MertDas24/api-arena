using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace APIArena.IntegrationTests.Infrastructure;

/// <summary>A browser-like client: keeps cookies, fetches and sends the CSRF token like the Next.js frontend does.</summary>
public sealed partial class TestUser(ApiFactory api, HttpClient client, string email, string password)
{
    public HttpClient Client { get; } = client;
    public string Email { get; } = email;
    public string Password { get; } = password;
    public Guid Id { get; private set; }
    private string? _csrf;

    public static TestUser Anonymous(ApiFactory api, string? email = null) =>
        new(api, api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false }),
            email ?? $"user-{Guid.NewGuid():N}@example.test", "correct-horse-battery");

    /// <summary>Registers, verifies via the mail sink and logs in.</summary>
    public static async Task<TestUser> CreateVerifiedAsync(ApiFactory api)
    {
        var user = Anonymous(api);
        (await user.PostAsync("/api/auth/register", new { email = user.Email, password = user.Password })).EnsureSuccessStatusCode();
        var (uid, token) = await user.ReadLinkAsync("verify-email");
        (await user.PostAsync("/api/auth/verify-email", new { userId = uid, token })).EnsureSuccessStatusCode();
        await user.LoginAsync();
        return user;
    }

    public async Task LoginAsync(string? password = null)
    {
        _csrf = null; // the previous session (and its token binding) may have expired
        var response = await PostAsync("/api/auth/login", new { email = Email, password = password ?? Password });
        response.EnsureSuccessStatusCode();
        var me = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Id = me.RootElement.GetProperty("id").GetGuid();
        _csrf = null; // antiforgery tokens are bound to the identity; refresh after login
    }

    public async Task<string> CsrfAsync()
    {
        if (_csrf is null)
        {
            var json = await Client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
            _csrf = json.GetProperty("token").GetString();
        }

        return _csrf!;
    }

    public void ResetCsrf() => _csrf = null;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, bool csrf = true, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (csrf && method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-TOKEN", await CsrfAsync());
        }

        configure?.Invoke(request);
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool csrf = true) => SendAsync(HttpMethod.Post, url, body ?? new { }, csrf);

    public Task<HttpResponseMessage> GetAsync(string url) => Client.GetAsync(url);

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        return json.TryGetProperty("code", out var c) ? c.GetString() : null;
    }

    [GeneratedRegex(@"uid=([a-f0-9]{32})&token=([A-Za-z0-9%_\-]+)")]
    private static partial Regex LinkPattern();

    /// <summary>Reads the newest link of the given category from the development mail sink.</summary>
    public async Task<(string Uid, string Token)> ReadLinkAsync(string category)
    {
        var mails = await Client.GetFromJsonAsync<JsonElement>($"/api/dev/mailbox?to={Uri.EscapeDataString(Email)}");
        var mail = mails.EnumerateArray().First(m => m.GetProperty("category").GetString() == category);
        var match = LinkPattern().Match(mail.GetProperty("textBody").GetString()!);
        Assert.True(match.Success, "link not found in mail");
        return (match.Groups[1].Value, Uri.UnescapeDataString(match.Groups[2].Value));
    }

    public string? SessionCookieHeader(HttpResponseMessage loginResponse) =>
        loginResponse.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("aa_session=", StringComparison.Ordinal))?.Split(';')[0]
            : null;

    public static HttpStatusCode Status(HttpResponseMessage r) => r.StatusCode;
}
