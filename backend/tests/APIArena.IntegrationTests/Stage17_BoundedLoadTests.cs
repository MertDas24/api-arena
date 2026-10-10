using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using APIArena.Domain.Entities;
using APIArena.Infrastructure.Jobs;
using APIArena.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.IntegrationTests;

[CollectionDefinition("BoundedLoad", DisableParallelization = true)]
public sealed class BoundedLoadCollection;

/// <summary>No configurable remote target. Real SQL test DB + ephemeral loopback Kestrel only.</summary>
[Collection("BoundedLoad")]
public sealed class Stage17_BoundedLoadTests(SqlDatabaseFixture db, ITestOutputHelper output)
{
    [Fact]
    public async Task Three_active_runs_bounded_inbound_polling_and_duplicate_workers()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = budget.Token;
        await using var api = new ApiFactory(db) { UseRealClock = true };
        var clock = api.Services.GetRequiredService<TimeProvider>();
        await using var sim = new SimulatorFactory(db, clock);
        sim.UseKestrel(0);
        using var client = sim.CreateClient();
        Assert.True(client.BaseAddress!.IsLoopback);
        var user = await TestUser.CreateVerifiedAsync(api);
        var project = await Arrange.ProjectAsync(user);
        var runs = new List<(Guid Id, string Key)>();
        foreach (var scenario in new[] { "retry-503-v1", "retry-503-v1", "timeout-v1" })
        {
            var id = await Arrange.RunAsync(user, project, scenario);
            var key = await Arrange.KeyAsync(user, id);
            await Arrange.StartAsync(user, id);
            runs.Add((id, key));
        }
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        var latency = new ConcurrentBag<double>();
        var polling = new ConcurrentBag<double>();
        await Task.WhenAll(
            Parallel.ForEachAsync(Enumerable.Range(0, 60), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
            {
                var r = runs[i % 2];
                var start = Stopwatch.GetTimestamp();
                using var request = Stage05_SimulatorTests.SimRequest(HttpMethod.Post, r.Id, "payment/charge", r.Key, "bounded-load", "{}");
                using var response = await client.SendAsync(request, token);
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
                latency.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }),
            Parallel.ForEachAsync(Enumerable.Range(0, 12), new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (i, token) =>
            {
                var start = Stopwatch.GetTimestamp();
                using var response = await user.GetAsync($"/api/runs/{runs[i % 3].Id}/events?afterSequence=0&limit=200");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                polling.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                token.ThrowIfCancellationRequested();
            }),
            Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                using var request = Stage05_SimulatorTests.SimRequest(HttpMethod.Get, runs[2].Id, "reports/slow", runs[2].Key);
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            })));
        var elapsed = timer.Elapsed.TotalSeconds;
        var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
        process.Refresh();
        await using var context = db.CreateContext();
        var ids = runs.Select(r => r.Id).ToArray();
        var writes = await context.RequestObservations.CountAsync(o => ids.Contains(o.RunId), ct);
        Assert.Equal(63, writes);
        var events = await context.RunEvents.CountAsync(e => ids.Contains(e.RunId), ct);

        // Worker-equivalent processors contend for 20 SQL jobs; handler makes no outbound request.
        var handler = new CountingHandler();
        await using var harness = await WebhookHarness.CreateAsync(db, extraWorkerServices: s => s.AddSingleton<IJobHandler>(handler));
        var now = harness.Api.Clock.GetUtcNow();
        var jobIds = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        context.OutboxJobs.AddRange(jobIds.Select(id => new OutboxJob
        {
            Id = id, Type = "bounded-load", Status = OutboxStatus.Pending, MaxAttempts = 1,
            CreatedAt = now.AddSeconds(-1), UpdatedAt = now, NextAttemptAt = now.AddSeconds(-1)
        }));
        await context.SaveChangesAsync(ct);
        var queueTimer = Stopwatch.StartNew();
        await Task.WhenAll(harness.NewProcessor().RunOnceAsync(10, ct), harness.NewProcessor().RunOnceAsync(10, ct));
        Assert.Equal(20, handler.Counts.Count);
        Assert.All(handler.Counts.Values, count => Assert.Equal(1, count));
        Assert.Equal(20, await context.OutboxJobs.CountAsync(j => jobIds.Contains(j.Id) && j.Status == OutboxStatus.Succeeded, ct));
        foreach (var run in runs)
            Assert.Equal(HttpStatusCode.OK, (await user.PostAsync($"/api/runs/{run.Id}/finish")).StatusCode);

        static double P95(IEnumerable<double> samples) { var s = samples.Order().ToArray(); return s[(int)Math.Ceiling(s.Length * .95) - 1]; }
        var result = new
        {
            scope = "loopback Kestrel + real SQL; test process includes API/simulator/client; SQL CPU/RAM excluded",
            activeRuns = 3, inboundRequests = 63, inboundConcurrency = 7, pollingRequests = 12, pollingConcurrency = 3,
            observationWrites = writes, eventWrites = events, delayMs = 3000, elapsedSeconds = elapsed,
            cpuSeconds, cpuPercentOfOneCore = cpuSeconds / elapsed * 100,
            peakTestProcessWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0,
            immediateRequestP95Ms = P95(latency), pollingP95Ms = P95(polling),
            workerJobs = 20, duplicateExecutions = 0, initialQueueLagMs = 1000,
            queueDrainMs = queueTimer.Elapsed.TotalMilliseconds,
            note = "Synthetic bounded sample, not production capacity; stream not applicable (polling only)."
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        var destination = Environment.GetEnvironmentVariable("APIARENA_LOAD_RESULT");
        if (!string.IsNullOrWhiteSpace(destination)) await File.WriteAllTextAsync(destination, json, ct);
    }

    private sealed class CountingHandler : IJobHandler
    {
        public string Type => "bounded-load";
        public ConcurrentDictionary<Guid, int> Counts { get; } = new();
        public Task HandleAsync(OutboxJob job, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Counts.AddOrUpdate(job.Id, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        }
    }
}
