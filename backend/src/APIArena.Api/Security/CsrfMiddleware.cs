using System.Text.RegularExpressions;
using APIArena.Application.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace APIArena.Api.Security;

/// <summary>
/// CSRF defence for every state-changing control-plane request (AUTH-03):
/// 1) Origin, when present, must be an allowed control-panel origin; Sec-Fetch-Site=cross-site is rejected.
/// 2) A valid antiforgery request token must be sent in X-CSRF-TOKEN (cookie + header pair, bound to the user).
/// SameSite=Strict cookies are a third, independent layer. GET/HEAD/OPTIONS never mutate state.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next, IAntiforgery antiforgery, IOptions<UrlOptions> urls)
{
    private readonly HashSet<string> _allowedOrigins = BuildOrigins(urls.Value);

    private static HashSet<string> BuildOrigins(UrlOptions urls)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls.AdditionalAllowedOrigins.Append(urls.FrontendBaseUrl))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                set.Add(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        return set;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresCheck(context.Request))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && !_allowedOrigins.Contains(origin))
            {
                await Reject(context, "csrf_origin_rejected", "İstek kaynağı (Origin) izinli değil.");
                return;
            }

            if (string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase))
            {
                await Reject(context, "csrf_origin_rejected", "Siteler arası istek reddedildi.");
                return;
            }

            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await Reject(context, "csrf_failed", "CSRF doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin.");
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresCheck(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        && !HttpMethods.IsGet(request.Method)
        && !HttpMethods.IsHead(request.Method)
        && !HttpMethods.IsOptions(request.Method);

    private static Task Reject(HttpContext context, string code, string title)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = 403, Title = title, Type = $"https://apiarena.dev/problems/{code}", Extensions = { ["code"] = code } },
        }).AsTask();
    }
}

public static partial class InputRules
{
    [GeneratedRegex(@"^[^@\s\x00-\x1F]{1,64}@[A-Za-z0-9.-]{1,190}\.[A-Za-z]{2,24}$")]
    private static partial Regex EmailPattern();

    public static bool IsEmail(string value) => value.Length <= 254 && EmailPattern().IsMatch(value);
}
