using System.Security.Claims;
using APIArena.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.Infrastructure.Identity;

/// <summary>
/// Server-side cookie session store. The browser cookie only holds a protected session key; the ticket lives in
/// <c>UserSessions</c>. Logout deletes the row, so a copied cookie stops working immediately rather than at expiry.
/// </summary>
public sealed class DbTicketStore(IServiceScopeFactory scopes, TimeProvider clock, IDataProtectionProvider dataProtection, TimeSpan idleTimeout) : ITicketStore
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("APIArena.Sessions.v1");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var userId = Guid.Parse(ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var now = clock.GetUtcNow();
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Ticket = _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + idleTimeout,
        };
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.UserSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id.ToString("N");
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (!Guid.TryParseExact(key, "N", out var id))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var blob = _protector.Protect(TicketSerializer.Default.Serialize(ticket));
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.UserSessions.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Ticket, blob)
            .SetProperty(s => s.LastSeenAt, now)
            .SetProperty(s => s.ExpiresAt, now + idleTimeout));
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!Guid.TryParseExact(key, "N", out var id))
        {
            return null;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.UserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
        if (session is null || session.ExpiresAt <= clock.GetUtcNow())
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(session.Ticket));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        if (!Guid.TryParseExact(key, "N", out var id))
        {
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.UserSessions.Where(s => s.Id == id).ExecuteDeleteAsync();
    }

    public static CookieAuthenticationOptions Attach(CookieAuthenticationOptions options, DbTicketStore store)
    {
        options.SessionStore = store;
        return options;
    }
}
