using APIArena.Application.Options;
using APIArena.Application.Runs;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure.Web;

/// <summary>Builds user-facing simulator URLs from configuration only (never from the incoming request's Host).</summary>
internal sealed class SimulatorUrls(IOptions<UrlOptions> urls) : ISimulatorUrls
{
    public string RunBaseUrl(Guid runId) => $"{urls.Value.SimulatorPublicBaseUrl.TrimEnd('/')}/sim/runs/{runId:D}";
}
