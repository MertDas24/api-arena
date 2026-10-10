using System.Security.Claims;
using APIArena.Application.Abstractions;
using APIArena.Application.Common;
using APIArena.Domain.Entities;
using APIArena.Domain.Runs;
using APIArena.Infrastructure.Identity;
using APIArena.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Api.Endpoints;

public sealed record DeleteAccountRequest(string? Password);

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Account deletion (AUTH-12): re-authentication with the password, then everything owned by the user is removed.
        app.MapDelete("/api/account", async (ClaimsPrincipal principal, [FromBody] DeleteAccountRequest body, UserManager<AppUser> users, SignInManager<AppUser> signIn,
            AppDbContext db, IRunAtomics atomics, IEnumerable<IAccountDeletionParticipant> participants, TimeProvider clock, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal) ?? throw new NotFoundException("Kullanıcı");
            if (string.IsNullOrEmpty(body.Password) || !await users.CheckPasswordAsync(user, body.Password))
            {
                throw new ForbiddenException("reauthentication_failed", "Parola doğrulanamadı.");
            }

            await atomics.WithUserLockAsync(user.Id, async () =>
            {
                var now = clock.GetUtcNow();
                var runIds = db.Runs.Where(r => db.Projects.Any(p => p.Id == r.ProjectId && p.OwnerId == user.Id)).Select(r => r.Id);
                await db.Runs.Where(r => runIds.Contains(r.Id) && (r.Status == RunStatus.Created || r.Status == RunStatus.Running))
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RunStatus.Cancelled).SetProperty(r => r.EndedAt, now)
                        .SetProperty(r => r.IncompleteReason, IncompleteReasons.AccountDeleted), ct);
                await db.RunAccessKeys.Where(k => runIds.Contains(k.RunId) && k.RevokedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(k => k.RevokedAt, now), ct);
                foreach (var p in participants)
                {
                    await p.OnAccountDeletingAsync(user.Id, now, ct);
                }

                return true;
            }, ct);

            // Cascades: projects → runs → observations/events/keys/reports; custom templates; sessions.
            var result = await users.DeleteAsync(user);
            if (!result.Succeeded)
            {
                throw new ConflictException("account_delete_failed", "Hesap silinemedi. Lütfen tekrar deneyin.");
            }

            await signIn.SignOutAsync();
            return Results.NoContent();
        }).WithTags("account");
    }
}

/// <summary>Lets later modules (shares, webhook jobs) revoke their resources before the user row is deleted.</summary>
public interface IAccountDeletionParticipant
{
    Task OnAccountDeletingAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
