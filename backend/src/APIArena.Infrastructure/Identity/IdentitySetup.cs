using APIArena.Application.Abstractions;
using APIArena.Application.Options;
using APIArena.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Cryptography.X509Certificates;

namespace APIArena.Infrastructure.Identity;

public static class IdentitySetup
{
    public static string SessionCookieName(IHostEnvironment env) => env.IsProduction() ? "__Host-aa_session" : "aa_session";
    public static string CsrfCookieName(IHostEnvironment env) => env.IsProduction() ? "__Host-aa_csrf" : "aa_csrf";

    /// <summary>
    /// Password policy (documented in docs/SECURITY.md): minimum 10 characters, at least 4 distinct characters,
    /// no composition rules (NIST SP 800-63B style). Lockout: 5 failures → 15 minutes.
    /// </summary>
    public static IServiceCollection AddApiArenaIdentity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment env)
    {
        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();

        var protection = services.AddDataProtection()
            .SetApplicationName("APIArena")
            .PersistKeysToDbContext<AppDbContext>();
        if (env.IsProduction())
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                configuration["ApiArena:DataProtection:CertificatePath"]!,
                configuration["ApiArena:DataProtection:CertificatePassword"],
                X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                throw new InvalidOperationException("DataProtection certificate must have a private key and must not be expired.");
            protection.ProtectKeysWithCertificate(certificate);
            // Keep old certificates mounted during rotation so existing tickets/tokens still decrypt.
            var previous = configuration.GetSection("ApiArena:DataProtection:PreviousCertificatePaths").Get<string[]>() ?? [];
            protection.UnprotectKeysWithAnyCertificate([certificate, .. previous.Select(path =>
                X509CertificateLoader.LoadPkcs12FromFile(path,
                    configuration["ApiArena:DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet))]);
        }

        services.AddIdentity<AppUser, IdentityRole<Guid>>(o =>
            {
                o.Password.RequiredLength = 10;
                o.Password.RequiredUniqueChars = 4;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.User.RequireUniqueEmail = true;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.SignIn.RequireConfirmedEmail = false; // unverified users may sign in but cannot start runs (AUTH-09)
                o.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.Name;
                o.Tokens.EmailConfirmationTokenProvider = EmailConfirmationTokenProvider.Name;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.Name)
            .AddTokenProvider<EmailConfirmationTokenProvider>(EmailConfirmationTokenProvider.Name);

        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = SessionCookieName(env);
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = env.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(auth.SessionIdleMinutes);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => WriteAuthProblem(ctx.HttpContext, StatusCodes.Status401Unauthorized, "unauthenticated", "Oturum açmanız gerekiyor.");
            o.Events.OnRedirectToAccessDenied = ctx => WriteAuthProblem(ctx.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "Bu işlem için yetkiniz yok.");
        });

        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<IServiceScopeFactory, TimeProvider, IDataProtectionProvider>((o, scopes, clock, dp) =>
                o.SessionStore = new DbTicketStore(scopes, clock, dp, TimeSpan.FromMinutes(auth.SessionIdleMinutes)));

        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = CsrfCookieName(env);
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = env.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.SuppressXFrameOptionsHeader = true; // set globally by SecurityHeaders
        });

        return services;
    }

    private static Task WriteAuthProblem(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        return context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = status, Title = title, Type = $"https://apiarena.dev/problems/{code}", Extensions = { ["code"] = code } },
        }).AsTask();
    }

    /// <summary>Revokes every server-side session of a user (password reset, logout-all, account deletion).</summary>
    public static Task<int> RevokeAllSessionsAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        db.UserSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
}

internal sealed class UserDirectory(AppDbContext db) : IUserDirectory
{
    public async Task<UserInfo?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new UserInfo(u.Id, u.Email!, u.EmailConfirmed))
            .FirstOrDefaultAsync(cancellationToken);
}
