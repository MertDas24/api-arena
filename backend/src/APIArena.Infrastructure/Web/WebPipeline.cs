using System.Text.RegularExpressions;
using APIArena.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace APIArena.Infrastructure.Web;

public static partial class CorrelationId
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "apiarena.correlation";

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex Valid();

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var v) && v is string s ? s : context.TraceIdentifier;

    /// <summary>Accepts a well-formed inbound correlation id (never echoes arbitrary header content), otherwise generates one.</summary>
    public static async Task Middleware(HttpContext context, RequestDelegate next)
    {
        var inbound = context.Request.Headers[HeaderName].ToString();
        var id = Valid().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.Headers[HeaderName] = id;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("APIArena.Request");
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }
}

public static class ProblemDetailsSetup
{
    public static IServiceCollection AddApiArenaProblemDetails(this IServiceCollection services, string plane)
    {
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            o.SerializerOptions.MaxDepth = 32;
        });
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Extensions["correlationId"] = CorrelationId.Get(ctx.HttpContext);
            ctx.ProblemDetails.Extensions["plane"] = plane;
            ctx.ProblemDetails.Extensions.Remove("traceId");
            // Never leak exception details outside Development.
            var env = ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
            if (!env.IsDevelopment())
            {
                ctx.ProblemDetails.Extensions.Remove("exception");
            }
        });
        services.AddExceptionHandler<AppExceptionHandler>();
        return services;
    }
}

public sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationFailedException v:
                problem = new HttpValidationProblemDetails(v.Errors.ToDictionary(k => k.Key, k => k.Value))
                {
                    Status = v.StatusCode,
                    Title = v.Message,
                };
                problem.Extensions["code"] = v.Code;
                break;
            case AppException app:
                if (app.StatusCode == 429)
                {
                    APIArena.Application.Ops.ArenaMetrics.Quota(app.Code);
                }

                problem = new ProblemDetails { Status = app.StatusCode, Title = app.Message };
                problem.Extensions["code"] = app.Code;
                break;
            case BadHttpRequestException bad:
                problem = new ProblemDetails { Status = bad.StatusCode, Title = "İstek okunamadı." };
                problem.Extensions["code"] = "bad_request";
                break;
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                // Client went away; nothing useful can be written.
                return true;
            default:
                logger.LogError(exception, "Unhandled platform error");
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Platform hatası oluştu. Bu bir simülasyon yanıtı değildir.",
                };
                problem.Extensions["code"] = "platform_error";
                break;
        }

        problem.Type = $"https://apiarena.dev/problems/{problem.Extensions["code"]}";
        httpContext.Response.StatusCode = problem.Status ?? 500;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}

public static class SecurityHeaders
{
    /// <summary>Headers for JSON APIs (control API and simulator). The simulator never lets scenario definitions override these.</summary>
    public static async Task ApiMiddleware(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Cross-Origin-Resource-Policy"] = "same-site";
            if (!h.ContainsKey("Cache-Control"))
            {
                h["Cache-Control"] = "no-store";
            }

            var env = context.RequestServices.GetRequiredService<IHostEnvironment>();
            if (env.IsProduction() && context.Request.IsHttps)
            {
                h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }

            h.Remove("Server");
            h.Remove("X-Powered-By");
            return Task.CompletedTask;
        });
        await next(context);
    }
}
