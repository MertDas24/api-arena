using APIArena.Application.Abstractions;
using APIArena.Application.Projects;
using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Application.Webhooks;

/// <summary>Project deletion cancels pending deliveries/jobs and revokes the project's webhook targets in the same transaction.</summary>
public sealed class WebhookDeletionParticipant(IAppDbContext db) : IProjectDeletionParticipant
{
    public async Task OnProjectDeletedAsync(Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var runIds = db.Runs.Where(r => r.ProjectId == projectId).Select(r => r.Id);
        await db.OutboxJobs.Where(j => j.RunId != null && runIds.Contains(j.RunId.Value) && (j.Status == OutboxStatus.Pending || j.Status == OutboxStatus.Leased))
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, OutboxStatus.Cancelled).SetProperty(j => j.UpdatedAt, now), ct);
        await db.WebhookDeliveryAttempts.Where(d => runIds.Contains(d.RunId) && d.Outcome == DeliveryAttemptOutcome.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Outcome, DeliveryAttemptOutcome.Cancelled).SetProperty(d => d.CompletedAt, now), ct);
        await db.WebhookTargets.Where(t => t.ProjectId == projectId && t.RevokedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.Status, WebhookTargetStatus.Revoked), ct);
    }
}
