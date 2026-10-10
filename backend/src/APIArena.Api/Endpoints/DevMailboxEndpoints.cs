using System.Text.Json;
using APIArena.Infrastructure.Email;
using APIArena.Application.Options;
using Microsoft.Extensions.Options;

namespace APIArena.Api.Endpoints;

/// <summary>
/// Development/Testing-only view of the file mail sink, so verification and reset links can be followed
/// end-to-end without a real mail provider. Never mapped in Production.
/// </summary>
public static class DevMailboxEndpoints
{
    public static void MapDevMailbox(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dev/mailbox", (IOptions<EmailOptions> options, IHostEnvironment env, string? to) =>
        {
            var directory = options.Value.ResolveFileDirectory(env.ContentRootPath);
            if (!Directory.Exists(directory))
            {
                return Results.Ok(Array.Empty<StoredEmail>());
            }

            var messages = Directory.EnumerateFiles(directory, "*.json")
                .OrderByDescending(f => f)
                .Take(200)
                .Select(f => JsonSerializer.Deserialize<StoredEmail>(File.ReadAllText(f)))
                .Where(m => m is not null && (to is null || string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase)))
                .Take(50)
                .ToList();
            return Results.Ok(messages);
        }).AllowAnonymous().WithTags("dev");
    }
}
