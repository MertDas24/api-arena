using System.Security.Cryptography;
using ApiArena.WebhookReceiver;
using APIArena.Infrastructure;
using APIArena.Infrastructure.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIArena.IntegrationTests.Infrastructure;

/// <summary>
/// Control API (webhooks enabled) + a worker-equivalent service provider (outbox + handlers) + the shipped sample
/// receiver on a local port, all sharing the test database and one fake clock.
/// </summary>
public sealed class WebhookHarness : IAsyncDisposable
{
    public required ApiFactory Api { get; init; }
    public required LocalServer Receiver { get; init; }
    public required ReceiverOptions ReceiverOptions { get; init; }
    public required ReceiverStore Store { get; init; }
    public required ServiceProvider Worker { get; init; }
    public required ReceiverFaults Faults { get; init; }

    public string ReceiverUrl => $"http://localhost:{Receiver.Port}/webhooks/apiarena";

    public OutboxProcessor NewProcessor() =>
        new(Worker.GetRequiredService<IServiceScopeFactory>(), Api.Clock, Worker.GetRequiredService<ILogger<OutboxProcessor>>());

    /// <summary>Runs the outbox until it is idle (bounded).</summary>
    public async Task DrainAsync(int rounds = 10)
    {
        var processor = Worker.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < rounds; i++)
        {
            if (await processor.RunOnceAsync(20, CancellationToken.None) == 0)
            {
                return;
            }
        }
    }

    public sealed class ReceiverFaults
    {
        public int FailNextDeliveries { get; set; }
    }

    public static async Task<WebhookHarness> CreateAsync(SqlDatabaseFixture db, bool naive = false, Action<IServiceCollection>? extraWorkerServices = null)
    {
        var receiverOptions = new ReceiverOptions();
        var store = new ReceiverStore(db.ConnectionString, naive);
        await store.EnsureSchemaAsync(CancellationToken.None);
        var faults = new ReceiverFaults();
        var api = new ApiFactory(db);

        var receiver = await LocalServer.StartAsync(app =>
        {
            // Fault injection in front of the real receiver endpoints (simulates a temporarily failing receiver).
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path == "/webhooks/apiarena" && ctx.Request.Headers.ContainsKey("X-Arena-Event-Id") && faults.FailNextDeliveries > 0)
                {
                    faults.FailNextDeliveries--;
                    ctx.Response.StatusCode = 500;
                    return;
                }

                await next(ctx);
            });
            ReceiverEndpoints.Map(app, store, receiverOptions, api.Clock);
        });

        var settings = new Dictionary<string, string?>
        {
            ["ApiArena:Webhooks:Enabled"] = "true",
            ["ApiArena:Webhooks:EncryptionKeys:Current"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["ApiArena:Webhooks:EncryptionKeys:CurrentId"] = "test-k1",
            ["ApiArena:Webhooks:DevLocalReceiver:Enabled"] = "true",
            ["ApiArena:Webhooks:DevLocalReceiver:Origins:0"] = $"http://localhost:{receiver.Port}",
        };
        foreach (var (k, v) in settings)
        {
            api.Overrides[k] = v;
        }

        var all = TestSettings.Base(db.ConnectionString, Path.GetTempPath());
        foreach (var (k, v) in settings)
        {
            all[k] = v;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(all).Build();
        var env = new WorkerEnv();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IHostEnvironment>(env);
        services.AddLogging();
        services.AddApiArenaCore(config, env);
        services.AddApiArenaJobHandlers(config);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(api.Clock);
        extraWorkerServices?.Invoke(services);
        var worker = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        return new WebhookHarness { Api = api, Receiver = receiver, ReceiverOptions = receiverOptions, Store = store, Worker = worker, Faults = faults };
    }

    private sealed class WorkerEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "worker-tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await Worker.DisposeAsync();
        await Receiver.DisposeAsync();
        await Api.DisposeAsync();
    }
}
