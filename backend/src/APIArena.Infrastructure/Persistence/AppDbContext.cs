using APIArena.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Infrastructure.Persistence;

public sealed partial class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(80);
        });

        builder.Entity<UserSession>(e =>
        {
            e.ToTable("UserSessions");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.UserId);
            e.HasIndex(s => s.ExpiresAt);
            e.Property(s => s.Ticket).IsRequired();
            e.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureDomain(builder);
    }

    partial void ConfigureDomain(ModelBuilder builder);
}
