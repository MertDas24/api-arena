using APIArena.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Infrastructure.Persistence;

public sealed partial class AppDbContext
{
    public DbSet<WebhookTarget> WebhookTargets => Set<WebhookTarget>();
    public DbSet<WebhookDeliveryAttempt> WebhookDeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<OutboxJob> OutboxJobs => Set<OutboxJob>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();

    partial void ConfigureV1(ModelBuilder b)
    {
        b.Entity<WebhookTarget>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Url).HasMaxLength(2048).IsRequired();
            e.Property(t => t.Host).HasMaxLength(253).IsRequired();
            e.Property(t => t.Path).HasMaxLength(1024).IsRequired();
            e.Property(t => t.AdapterPath).HasMaxLength(512);
            e.Property(t => t.SigningSecretCiphertext).HasMaxLength(256).IsRequired();
            e.Property(t => t.SigningSecretKeyId).HasMaxLength(40).IsRequired();
            e.Property(t => t.SecretHint).HasMaxLength(16).IsRequired();
            e.Property(t => t.LastVerificationError).HasMaxLength(60);
            e.Property(t => t.RowVersion).IsRowVersion();
            e.HasIndex(t => new { t.ProjectId, t.RevokedAt });
            e.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Run>().HasOne<WebhookTarget>().WithMany().HasForeignKey(r => r.WebhookTargetId).OnDelete(DeleteBehavior.NoAction);

        b.Entity<WebhookDeliveryAttempt>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.EventId).HasMaxLength(80).IsRequired();
            e.Property(d => d.AggregateId).HasMaxLength(80).IsRequired();
            e.Property(d => d.EventType).HasMaxLength(60).IsRequired();
            e.Property(d => d.PayloadJson).HasMaxLength(8000).IsRequired();
            e.Property(d => d.Error).HasMaxLength(60);
            e.HasIndex(d => new { d.RunId, d.PlannedOrder, d.AttemptNumber }).IsUnique();
            e.HasOne<Run>().WithMany().HasForeignKey(d => d.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WebhookTarget>().WithMany().HasForeignKey(d => d.TargetId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<OutboxJob>(e =>
        {
            e.HasKey(j => j.Id);
            e.Property(j => j.Type).HasMaxLength(40).IsRequired();
            e.Property(j => j.PayloadJson).HasMaxLength(4000).IsRequired();
            e.Property(j => j.LeaseOwner).HasMaxLength(80);
            e.Property(j => j.LastError).HasMaxLength(200);
            e.HasIndex(j => new { j.Status, j.NextAttemptAt });
            e.HasIndex(j => j.RunId);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Action).HasMaxLength(60).IsRequired();
            e.Property(a => a.ResourceType).HasMaxLength(40).IsRequired();
            e.Property(a => a.ResourceId).HasMaxLength(80);
            e.Property(a => a.DataJson).HasMaxLength(2000);
            e.HasIndex(a => new { a.UserId, a.OccurredAt });
            e.HasIndex(a => a.OccurredAt);
        });

        b.Entity<ShareLink>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.TokenHash).HasMaxLength(32).IsFixedLength().IsRequired();
            e.Property(s => s.DisplayPrefix).HasMaxLength(16).IsRequired();
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasIndex(s => new { s.RunId, s.RevokedAt });
            e.HasOne<Run>().WithMany().HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
