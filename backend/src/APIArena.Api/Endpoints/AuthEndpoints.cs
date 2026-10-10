using System.Security.Claims;
using System.Threading.RateLimiting;
using APIArena.Api.Security;
using APIArena.Application.Abstractions;
using APIArena.Application.Common;
using APIArena.Infrastructure.Identity;
using APIArena.Application.Options;
using APIArena.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace APIArena.Api.Endpoints;

public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record EmailRequest(string? Email);
public sealed record VerifyEmailRequest(string? UserId, string? Token);
public sealed record ResetPasswordRequest(string? UserId, string? Token, string? NewPassword);
public sealed record MeResponse(Guid Id, string Email, string? DisplayName, bool EmailConfirmed, DateTimeOffset CreatedAt, bool RunsRequireVerifiedEmail);

public static class AuthEndpoints
{
    public const string AuthPolicy = "auth";
    public const string SensitivePolicy = "auth-sensitive";

    public static IServiceCollection AddAuthPlane(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddApiArenaIdentity(configuration, environment);
        services.AddAuthorization(o =>
        {
            // Secure by default: every endpoint needs a session unless it explicitly opts out.
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.AuthPermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
            o.AddPolicy(SensitivePolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = auth.SensitivePermitsPer15Minutes,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
            }));
            o.AddPolicy(RunEndpoints.FeedPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 240,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            WebhookEndpoints.AddWebhookRateLimits(o);
            ShareEndpoints.AddShareRateLimits(o);
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = ctx.HttpContext,
                    ProblemDetails =
                    {
                        Status = 429,
                        Title = "Çok fazla deneme. Lütfen biraz sonra tekrar deneyin.",
                        Type = "https://apiarena.dev/problems/rate_limited",
                        Extensions = { ["code"] = "rate_limited" },
                    },
                });
            };
        });
        return services;
    }

    private static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static WebApplication UseAuthPlane(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseRateLimiter();
        return app;
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("auth");

        g.MapGet("/csrf", (IAntiforgery antiforgery, HttpContext ctx) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(ctx);
            return Results.Ok(new { token = tokens.RequestToken, headerName = "X-CSRF-TOKEN" });
        }).AllowAnonymous();

        g.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(AuthPolicy);
        g.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(AuthPolicy);
        g.MapPost("/logout", Logout).AllowAnonymous();
        g.MapPost("/logout-all", LogoutAll);
        g.MapGet("/me", Me);
        g.MapPost("/verify-email", VerifyEmail).AllowAnonymous().RequireRateLimiting(SensitivePolicy);
        g.MapPost("/resend-verification", ResendVerification).RequireRateLimiting(SensitivePolicy);
        g.MapPost("/forgot-password", ForgotPassword).AllowAnonymous().RequireRateLimiting(SensitivePolicy);
        g.MapPost("/reset-password", ResetPassword).AllowAnonymous().RequireRateLimiting(SensitivePolicy);
    }

    private static async Task<IResult> Register(RegisterRequest body, UserManager<AppUser> users, IEmailSender email,
        IOptions<UrlOptions> urls, TimeProvider clock, CancellationToken ct)
    {
        var address = (body.Email ?? "").Trim();
        var password = body.Password ?? "";
        var errors = new Dictionary<string, string[]>();
        if (!InputRules.IsEmail(address))
        {
            errors["email"] = ["Geçerli bir e-posta adresi girin."];
        }

        var displayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim();
        if (displayName is { Length: > 80 })
        {
            errors["displayName"] = ["Görünen ad en fazla 80 karakter olabilir."];
        }

        // Password policy is checked against a throwaway user BEFORE looking up the address,
        // so policy errors never reveal whether an account exists (AUTH-06).
        var policyErrors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            var r = await validator.ValidateAsync(users, new AppUser { UserName = "policy-check" }, password);
            policyErrors.AddRange(r.Errors.Select(PasswordMessages.Translate));
        }

        if (policyErrors.Count > 0)
        {
            errors["password"] = policyErrors.Distinct().ToArray();
        }

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        var existing = await users.FindByEmailAsync(address);
        EmailSendResult sent;
        if (existing is not null)
        {
            sent = await email.SendAsync(new EmailMessage(address, "API Arena: kayıt denemesi",
                "Bu e-posta adresiyle yeni bir kayıt denendi. Hesabınız zaten var. Parolanızı unuttuysanız giriş ekranındaki " +
                "'Parolamı unuttum' bağlantısını kullanın. Bu işlemi siz yapmadıysanız bu mesajı yok sayabilirsiniz.", "register-existing"), ct);
        }
        else
        {
            var user = new AppUser { UserName = address, Email = address, DisplayName = displayName, CreatedAt = clock.GetUtcNow() };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                // Duplicate races are reported generically; other failures are input problems.
                if (created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
                {
                    return Accepted();
                }

                throw new ValidationFailedException(new Dictionary<string, string[]> { ["email"] = ["Bu e-posta adresi kullanılamıyor."] });
            }

            sent = await SendVerificationAsync(users, email, urls.Value, user, ct);
        }

        if (!sent.Sent)
        {
            throw new EmailNotSentException();
        }

        return Accepted();

        static IResult Accepted() => Results.Accepted(value: new
        {
            message = "Kayıt isteği alındı. Doğrulama talimatları e-posta adresinize gönderildi.",
        });
    }

    private static async Task<EmailSendResult> SendVerificationAsync(UserManager<AppUser> users, IEmailSender email, UrlOptions urls, AppUser user, CancellationToken ct)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = $"{urls.FrontendBaseUrl.TrimEnd('/')}/verify-email?uid={user.Id:N}&token={Uri.EscapeDataString(token)}";
        return await email.SendAsync(new EmailMessage(user.Email!, "API Arena: e-posta adresinizi doğrulayın",
            $"API Arena hesabınızı doğrulamak için bağlantıyı açın (24 saat geçerli, tek kullanımlık):\n\n{link}\n\nBu hesabı siz oluşturmadıysanız bu mesajı yok sayın.",
            "verify-email"), ct);
    }

    private static async Task<IResult> Login(LoginRequest body, SignInManager<AppUser> signIn, UserManager<AppUser> users)
    {
        var address = (body.Email ?? "").Trim();
        var user = InputRules.IsEmail(address) ? await users.FindByEmailAsync(address) : null;
        if (user is not null)
        {
            var result = await signIn.PasswordSignInAsync(user, body.Password ?? "", isPersistent: false, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                return Results.Ok(ToMe(user, signIn.Context.RequestServices));
            }
        }
        else
        {
            // Equalise work for unknown accounts so timing does not reveal existence.
            users.PasswordHasher.HashPassword(new AppUser(), body.Password ?? "");
        }

        // Same response for unknown account, wrong password and lock-out (AUTH-06).
        throw new LoginFailedException();
    }

    private static async Task<IResult> Logout(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync(); // removes the server-side session row
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAll(ClaimsPrincipal principal, UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal) ?? throw new NotFoundException("Kullanıcı");
        await users.UpdateSecurityStampAsync(user);
        await IdentitySetup.RevokeAllSessionsAsync(db, user.Id, ct);
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, UserManager<AppUser> users, HttpContext ctx)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? Results.Unauthorized() : Results.Ok(ToMe(user, ctx.RequestServices));
    }

    private static MeResponse ToMe(AppUser user, IServiceProvider sp) =>
        new(user.Id, user.Email!, user.DisplayName, user.EmailConfirmed, user.CreatedAt,
            sp.GetRequiredService<IOptions<AuthOptions>>().Value.RequireVerifiedEmailForRuns);

    private static async Task<IResult> VerifyEmail(VerifyEmailRequest body, UserManager<AppUser> users)
    {
        var user = Guid.TryParse(body.UserId, out var id) ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || string.IsNullOrEmpty(body.Token))
        {
            throw new InvalidTokenException();
        }

        if (user.EmailConfirmed)
        {
            // Idempotent: a used link does nothing further.
            return Results.Ok(new { verified = true, alreadyVerified = true });
        }

        var result = await users.ConfirmEmailAsync(user, body.Token);
        if (!result.Succeeded)
        {
            throw new InvalidTokenException();
        }

        return Results.Ok(new { verified = true, alreadyVerified = false });
    }

    private static async Task<IResult> ResendVerification(ClaimsPrincipal principal, UserManager<AppUser> users, IEmailSender email, IOptions<UrlOptions> urls, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal) ?? throw new NotFoundException("Kullanıcı");
        if (user.EmailConfirmed)
        {
            return Results.Ok(new { message = "E-posta adresiniz zaten doğrulanmış." });
        }

        var sent = await SendVerificationAsync(users, email, urls.Value, user, ct);
        if (!sent.Sent)
        {
            throw new EmailNotSentException();
        }

        return Results.Ok(new { message = "Doğrulama e-postası gönderildi." });
    }

    private static async Task<IResult> ForgotPassword(EmailRequest body, UserManager<AppUser> users, IEmailSender email,
        IOptions<UrlOptions> urls, IOptions<AuthOptions> auth, ILoggerFactory loggers, CancellationToken ct)
    {
        var address = (body.Email ?? "").Trim();
        var user = InputRules.IsEmail(address) ? await users.FindByEmailAsync(address) : null;
        if (user is not null)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = $"{urls.Value.FrontendBaseUrl.TrimEnd('/')}/reset-password?uid={user.Id:N}&token={Uri.EscapeDataString(token)}";
            var sent = await email.SendAsync(new EmailMessage(user.Email!, "API Arena: parola sıfırlama",
                $"Parolanızı sıfırlamak için bağlantıyı açın ({auth.Value.PasswordResetTokenMinutes} dakika geçerli, tek kullanımlık):\n\n{link}\n\n" +
                "Bu isteği siz yapmadıysanız bu mesajı yok sayın; parolanız değişmez.", "reset-password"), ct);
            if (!sent.Sent)
            {
                loggers.CreateLogger("APIArena.Auth").LogWarning("Password reset e-mail could not be delivered ({Reason})", sent.FailureReason);
            }
        }

        // Identical response whether or not the account exists; it does not claim that an e-mail was delivered.
        return Results.Accepted(value: new
        {
            message = "Bu adres kayıtlıysa parola sıfırlama bağlantısı gönderilecektir. Gelen kutunuzu ve spam klasörünü kontrol edin.",
        });
    }

    private static async Task<IResult> ResetPassword(ResetPasswordRequest body, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var user = Guid.TryParse(body.UserId, out var id) ? await users.FindByIdAsync(id.ToString()) : null;
        if (user is null || string.IsNullOrEmpty(body.Token))
        {
            throw new InvalidTokenException();
        }

        var result = await users.ResetPasswordAsync(user, body.Token, body.NewPassword ?? "");
        if (!result.Succeeded)
        {
            var policy = result.Errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).Select(PasswordMessages.Translate).ToArray();
            if (policy.Length > 0)
            {
                throw new ValidationFailedException(new Dictionary<string, string[]> { ["newPassword"] = policy });
            }

            throw new InvalidTokenException();
        }

        // ResetPasswordAsync rotated the security stamp (token is now single-use); also end every active session.
        await IdentitySetup.RevokeAllSessionsAsync(db, user.Id, ct);
        await users.ResetAccessFailedCountAsync(user);
        return Results.Ok(new { message = "Parolanız güncellendi. Tüm oturumlar kapatıldı; yeni parolanızla giriş yapın." });
    }
}

public sealed class LoginFailedException()
    : AppException(401, "login_failed", "Giriş başarısız. Bilgilerinizi kontrol edin veya bir süre sonra tekrar deneyin.");

public sealed class InvalidTokenException()
    : AppException(400, "invalid_or_expired_token", "Bağlantı geçersiz, süresi dolmuş veya daha önce kullanılmış.");

public sealed class EmailNotSentException()
    : AppException(503, "email_not_sent", "E-posta şu anda gönderilemedi. Lütfen daha sonra tekrar deneyin.");

public static class PasswordMessages
{
    public static string Translate(IdentityError e) => e.Code switch
    {
        "PasswordTooShort" => "Parola en az 10 karakter olmalı.",
        "PasswordRequiresUniqueChars" => "Parola en az 4 farklı karakter içermeli.",
        _ => "Parola politikası karşılanmadı.",
    };
}
