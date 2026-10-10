using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace APIArena.Infrastructure.Identity;

/// <summary>
/// Same construction as Identity's DataProtectorTokenProvider (ASP.NET Core Data Protection does the crypto),
/// but reads the current time from the injected <see cref="TimeProvider"/> so expiry is testable without sleeping.
/// Tokens embed the security stamp, so any stamp change (password reset, logout-all) invalidates them: reset tokens
/// are therefore single-use. Tokens are base64url so they survive e-mail links unchanged.
/// </summary>
public abstract class TimeAwareTokenProvider(IDataProtectionProvider dataProtection, TimeProvider clock, string name, TimeSpan lifespan)
    : IUserTwoFactorTokenProvider<AppUser>
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("APIArena.Identity.Tokens", name);

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<AppUser> manager, AppUser user) => Task.FromResult(false);

    public async Task<string> GenerateAsync(string purpose, UserManager<AppUser> manager, AppUser user)
    {
        var stamp = manager.SupportsUserSecurityStamp ? await manager.GetSecurityStampAsync(user) : "";
        using var ms = new MemoryStream();
        await using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(clock.GetUtcNow().UtcTicks);
            writer.Write(user.Id.ToString("N"));
            writer.Write(purpose);
            writer.Write(stamp ?? "");
        }

        return WebEncoders.Base64UrlEncode(_protector.Protect(ms.ToArray()));
    }

    public async Task<bool> ValidateAsync(string purpose, string token, UserManager<AppUser> manager, AppUser user)
    {
        try
        {
            var data = _protector.Unprotect(WebEncoders.Base64UrlDecode(token));
            using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            var created = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            if (created + lifespan < clock.GetUtcNow())
            {
                return false;
            }

            if (reader.ReadString() != user.Id.ToString("N") || reader.ReadString() != purpose)
            {
                return false;
            }

            var stamp = reader.ReadString();
            return !manager.SupportsUserSecurityStamp || stamp == (await manager.GetSecurityStampAsync(user) ?? "");
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or EndOfStreamException)
        {
            return false;
        }
    }
}

public sealed class PasswordResetTokenProvider(IDataProtectionProvider dp, TimeProvider clock, Microsoft.Extensions.Options.IOptions<APIArena.Application.Options.AuthOptions> auth)
    : TimeAwareTokenProvider(dp, clock, Name, TimeSpan.FromMinutes(auth.Value.PasswordResetTokenMinutes))
{
    public const string Name = "ApiArenaReset";
}

public sealed class EmailConfirmationTokenProvider(IDataProtectionProvider dp, TimeProvider clock, Microsoft.Extensions.Options.IOptions<APIArena.Application.Options.AuthOptions> auth)
    : TimeAwareTokenProvider(dp, clock, Name, TimeSpan.FromMinutes(auth.Value.EmailConfirmationTokenMinutes))
{
    public const string Name = "ApiArenaEmail";
}
