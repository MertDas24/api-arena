using APIArena.Application.Abstractions;
using APIArena.Domain.Entities;
using APIArena.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Infrastructure.Persistence;

public sealed partial class AppDbContext : IAppDbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ScenarioTemplate> ScenarioTemplates => Set<ScenarioTemplate>();
    public DbSet<ScenarioVersion> ScenarioVersions => Set<ScenarioVersion>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunOperation> RunOperations => Set<RunOperation>();
    public DbSet<RequestObservation> RequestObservations => Set<RequestObservation>();
    public DbSet<RunEvent> RunEvents => Set<RunEvent>();
    public DbSet<RunAccessKey> RunAccessKeys => Set<RunAccessKey>();
    public DbSet<ReportSnapshot> ReportSnapshots => Set<ReportSnapshot>();
    public DbSet<CheckResult> CheckResults => Set<CheckResult>();

    partial void ConfigureDomain(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(80).IsRequired();
            e.Property(p => p.Description).HasMaxLength(500);
            e.Property(p => p.SimulatorAllowedOrigins).HasMaxLength(2000).IsRequired();
            e.Property(p => p.RowVersion).IsRowVersion();
            e.HasIndex(p => new { p.OwnerId, p.DeletedAt, p.CreatedAt });
            e.HasOne<AppUser>().WithMany().HasForeignKey(p => p.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_Projects_Name", "LEN([Name]) > 0"));
        });

        b.Entity<ScenarioTemplate>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Slug).HasMaxLength(60).IsRequired();
            e.Property(t => t.Title).HasMaxLength(120).IsRequired();
            e.Property(t => t.Summary).HasMaxLength(500).IsRequired();
            // Built-in slugs are globally unique; custom slugs are unique per owner.
            e.HasIndex(t => new { t.OwnerId, t.Slug }).IsUnique().HasFilter(null);
            e.HasIndex(t => t.Slug).IsUnique().HasFilter("[OwnerId] IS NULL").HasDatabaseName("UX_ScenarioTemplates_BuiltInSlug");
            e.HasMany(t => t.Versions).WithOne().HasForeignKey(v => v.TemplateId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ScenarioVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.HasIndex(v => new { v.TemplateId, v.Version }).IsUnique();
            e.Property(v => v.Engine).HasMaxLength(40).IsRequired();
            e.Property(v => v.EvaluatorVersion).HasMaxLength(40).IsRequired();
            e.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("CK_ScenarioVersions_Version", "[Version] >= 1"));
        });

        b.Entity<Run>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.ScenarioSlug).HasMaxLength(60).IsRequired();
            e.Property(r => r.Engine).HasMaxLength(40).IsRequired();
            e.Property(r => r.EvaluatorVersion).HasMaxLength(40).IsRequired();
            e.Property(r => r.Label).HasMaxLength(80);
            e.Property(r => r.IncompleteReason).HasMaxLength(40);
            e.Property(r => r.RowVersion).IsRowVersion();
            e.HasIndex(r => new { r.ProjectId, r.CreatedAt });
            e.HasIndex(r => new { r.Status, r.ExpiresAt });
            e.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ScenarioVersion>().WithMany().HasForeignKey(r => r.ScenarioVersionId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Runs_Counts", "[InboundCount] >= 0 AND [InboundCount] <= [InboundLimit] AND [NextSequence] >= 0");
                t.HasCheckConstraint("CK_Runs_Status", "[Status] BETWEEN 0 AND 5");
            });
        });

        b.Entity<RunOperation>(e =>
        {
            e.HasKey(o => new { o.RunId, o.OperationKey });
            e.Property(o => o.OperationKey).HasMaxLength(140);
            e.HasOne<Run>().WithMany().HasForeignKey(o => o.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestObservation>(e =>
        {
            e.HasKey(o => o.Id);
            e.HasIndex(o => new { o.RunId, o.Sequence }).IsUnique();
            e.Property(o => o.OperationKey).HasMaxLength(140);
            e.Property(o => o.Method).HasMaxLength(10).IsRequired();
            e.Property(o => o.Route).HasMaxLength(200).IsRequired();
            e.Property(o => o.HeadersJson).HasMaxLength(8000).IsRequired();
            e.Property(o => o.BodyPreview).HasMaxLength(4000);
            e.Property(o => o.RejectReason).HasMaxLength(60);
            e.Property(o => o.ResponsePreview).HasMaxLength(1000);
            e.HasOne<Run>().WithMany().HasForeignKey(o => o.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
            e.Property(x => x.Type).HasMaxLength(40).IsRequired();
            e.Property(x => x.DataJson).HasMaxLength(4000);
            e.HasOne<Run>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunAccessKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.Property(k => k.KeyHash).HasMaxLength(32).IsFixedLength().IsRequired();
            e.Property(k => k.DisplayPrefix).HasMaxLength(16).IsRequired();
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => new { k.RunId, k.RevokedAt });
            e.HasOne<Run>().WithMany().HasForeignKey(k => k.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReportSnapshot>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.RunId).IsUnique(); // idempotent finalisation (REP-03)
            e.Property(r => r.ScenarioSlug).HasMaxLength(60).IsRequired();
            e.Property(r => r.EvaluatorVersion).HasMaxLength(40).IsRequired();
            e.Property(r => r.IncompleteReason).HasMaxLength(40);
            e.HasMany(r => r.Checks).WithOne().HasForeignKey(c => c.ReportId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Run>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CheckResult>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => new { c.ReportId, c.CheckId }).IsUnique();
            e.Property(c => c.CheckId).HasMaxLength(60).IsRequired();
            e.Property(c => c.Title).HasMaxLength(160).IsRequired();
            e.Property(c => c.Expected).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Observed).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Explanation).HasMaxLength(2000).IsRequired();
            e.Property(c => c.Suggestion).HasMaxLength(2000);
            e.Property(c => c.EvidenceJson).HasMaxLength(2000).IsRequired();
            e.Property(c => c.EvaluatorVersion).HasMaxLength(40).IsRequired();
        });

        ConfigureV1(b);
    }

    partial void ConfigureV1(ModelBuilder b);
}
