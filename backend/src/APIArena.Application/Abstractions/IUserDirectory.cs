namespace APIArena.Application.Abstractions;

public sealed record UserInfo(Guid Id, string Email, bool EmailConfirmed);

/// <summary>Read-only view of accounts for application services (Identity lives in Infrastructure).</summary>
public interface IUserDirectory
{
    Task<UserInfo?> FindAsync(Guid userId, CancellationToken cancellationToken);
}
