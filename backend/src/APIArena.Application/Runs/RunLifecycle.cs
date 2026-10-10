namespace APIArena.Application.Runs;

/// <summary>Modules that react to run state changes inside the same request (e.g. webhook delivery planning/cancellation).</summary>
public interface IRunLifecycleParticipant
{
    Task OnStartedAsync(Guid runId, CancellationToken ct);

    Task OnEndedAsync(Guid runId, CancellationToken ct);
}
