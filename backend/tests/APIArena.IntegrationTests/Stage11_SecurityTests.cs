using System.Net;
using APIArena.Infrastructure.Web;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;

namespace APIArena.IntegrationTests;

/// <summary>Stage 11: production configuration, debug surface and abuse-path negative tests.</summary>
public sealed class Stage11_SecurityTests(SqlDatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ApiFactory ProductionApi(SqlDatabaseFixture database)
    {
        var api = new ApiFactory(database);
        var cs = new SqlConnectionStringBuilder(database.ConnectionString) { TrustServerCertificate = false, Encrypt = SqlConnectionEncryptOption.Optional };
        api.Overrides["ConnectionStrings:ApiArena"] = cs.ConnectionString;
        api.Overrides["ApiArena:Urls:FrontendBaseUrl"] = "https://app.apiarena.example";
        api.Overrides["ApiArena:Urls:SimulatorPublicBaseUrl"] = "https://sim.apiarena-data.example";
        api.Overrides["ApiArena:Email:Mode"] = "Smtp";
        api.Overrides["ApiArena:Email:SmtpHost"] = "smtp.invalid"; // RFC 2606: guaranteed not to resolve
        Directory.CreateDirectory(api.MailDirectory);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=APIArena-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(api.MailDirectory, "identity.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        api.Overrides["ApiArena:DataProtection:CertificatePath"] = path;
        api.Environment = "Production";
        return api;
    }

    [Fact]
    public async Task Production_hides_debug_surface_uses_host_prefixed_secure_cookies_and_hsts()
    {
        // Own database: the DataProtection key ring is shared per database, and this test asserts that the key ring a
        // production host creates is certificate-encrypted (other tests' plaintext dev keys must not be reused).
        await using var isolated = new SqlDatabaseFixture();
        await isolated.InitializeAsync();
        await using var api = ProductionApi(isolated);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var user = new TestUser(api, client, $"prod-{Guid.NewGuid():N}@example.test", "correct-horse-battery");

        // E-mail cannot be delivered (no reachable SMTP): the API must not claim it was sent.
        var register = await user.PostAsync("/api/auth/register", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, register.StatusCode);
        Assert.Equal("email_not_sent", await TestUser.ProblemCodeAsync(register));

        var login = await user.PostAsync("/api/auth/login", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = string.Join("\n", login.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        Assert.Contains("__host-aa_session=", setCookie);
        Assert.Contains("secure", setCookie);
        Assert.Contains("httponly", setCookie);
        Assert.Contains("hsts", string.Join(",", (await client.GetAsync("/health/live", Ct)).Headers.Select(h => h.Key == "Strict-Transport-Security" ? "hsts" : h.Key)));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/dev/mailbox", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/openapi/v1.json", Ct)).StatusCode);
        await using var ctx = isolated.CreateContext();
        Assert.Contains(await ctx.DataProtectionKeys.ToListAsync(Ct), k => k.Xml!.Contains("encryptedSecret"));
    }

    [Fact]
    public async Task Unhandled_errors_in_production_return_problem_details_without_internals()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddApiArenaProblemDetails("control");
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.Use(CorrelationId.Middleware);
        app.MapGet("/boom", (Func<IResult>)(() => throw new InvalidOperationException("Server=db.internal;Password=hunter2 at SecretClass.Method()")));
        await app.StartAsync(Ct);

        var response = await app.GetTestClient().GetAsync("/boom", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("platform_error", body);
        Assert.Contains("correlationId", body);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("db.internal", body);
        Assert.DoesNotContain("SecretClass", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task Simulator_limits_requests_per_client_before_authentication()
    {
        await using var api = new ApiFactory(db);
        await using var sim = new SimulatorFactory(db, api.Clock);
        sim.Overrides["ApiArena:Limits:SimulatorRequestsPerIpPerMinute"] = "10";
        var c = sim.CreateClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 11; i++)
        {
            last = await c.SendAsync(Stage05_SimulatorTests.SimRequest(HttpMethod.Get, Guid.NewGuid(), "orders", "aa_rk_" + new string('x', 43)), Ct);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("client-rate-limited", last.Headers.GetValues("X-Arena-Platform").Single());
    }

    [Fact]
    public async Task Control_api_has_no_cors_grant_for_foreign_origins()
    {
        await using var api = new ApiFactory(db);
        var c = api.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/projects");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        var response = await c.SendAsync(request, Ct);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Get_requests_never_mutate_state()
    {
        await using var api = new ApiFactory(db);
        var (user, _, run, _) = await Arrange.RunningAsync(api);
        foreach (var path in new[] { "finish", "cancel", "start", "access-keys" })
        {
            var r = await user.GetAsync($"/api/runs/{run}/{path}");
            Assert.True(r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{path}: {r.StatusCode}");
        }

        Assert.Equal("Running", (await user.GetJsonAsync($"/api/runs/{run}")).GetProperty("status").GetString());
    }
}
