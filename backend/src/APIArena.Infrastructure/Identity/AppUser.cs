using Microsoft.AspNetCore.Identity;

namespace APIArena.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>Server-side session record. The auth cookie only carries a protected reference to this row,
/// so deleting the row (logout, password reset, account deletion) revokes the session immediately.</summary>
public sealed class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public byte[] Ticket { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
