using System.Security.Claims;
using System.Text.Json;
using APIArena.Application.Common;
using APIArena.Application.Scenarios;
using APIArena.Domain.Scenarios;

namespace APIArena.Api.Endpoints;

public static class CustomScenarioEndpoints
{
    public static void MapCustomScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/scenarios/custom").WithTags("custom-scenarios");

        // Dry run with exactly the simulator's decision function (UI preview == real run semantics).
        g.MapPost("/preview", (CustomScenarioService svc, PreviewRequest body) => svc.Preview(body));

        g.MapPost("/", async (ClaimsPrincipal user, CustomScenarioService svc, CustomScenarioRequest body, CancellationToken ct) =>
        {
            var created = await svc.CreateAsync(user.Id(), body, ct);
            return Results.Created($"/api/scenarios/{created.Id}", created);
        });

        g.MapGet("/{id:guid}", (ClaimsPrincipal user, CustomScenarioService svc, Guid id, CancellationToken ct) => svc.GetOwnAsync(user.Id(), id, ct));

        g.MapPost("/{id:guid}/versions", (ClaimsPrincipal user, CustomScenarioService svc, Guid id, CustomScenarioRequest body, CancellationToken ct) =>
            svc.AddVersionAsync(user.Id(), id, body, ct));

        g.MapDelete("/{id:guid}", async (ClaimsPrincipal user, CustomScenarioService svc, Guid id, CancellationToken ct) =>
        {
            await svc.ArchiveAsync(user.Id(), id, ct);
            return Results.NoContent();
        });

        g.MapGet("/{id:guid}/export", async (ClaimsPrincipal user, CustomScenarioService svc, Guid id, int? version, CancellationToken ct) =>
        {
            var doc = await svc.ExportAsync(user.Id(), id, version, ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                "application/json", $"apiarena-custom-scenario-{id.ToString("N")[..8]}.json");
        });

        // Import: bounded size, strict format; reuses the same validation path as the editor.
        g.MapPost("/import", async (ClaimsPrincipal user, CustomScenarioService svc, HttpRequest request, CancellationToken ct) =>
        {
            if (request.ContentLength is null or > CustomScenarioCompiler.MaxDocumentBytes + 2048)
            {
                throw ValidationFailedException.For("file", "Dosya en fazla 32 KiB olabilir.");
            }

            ImportDocument? doc;
            try
            {
                doc = await JsonSerializer.DeserializeAsync<ImportDocument>(request.Body, ImportJson, ct);
            }
            catch (JsonException)
            {
                throw ValidationFailedException.For("file", "Dosya geçerli bir API Arena özel senaryo JSON'u değil (bilinmeyen alan veya hatalı biçim).");
            }

            if (doc is null || doc.Format != "apiarena.custom-scenario")
            {
                throw ValidationFailedException.For("file", "Beklenen biçim: apiarena.custom-scenario.");
            }

            var created = await svc.CreateAsync(user.Id(), new CustomScenarioRequest(doc.Title, doc.Summary, doc.Definition), ct);
            return Results.Created($"/api/scenarios/{created.Id}", created);
        });
    }

    private sealed record ImportDocument(string Format, string? Title, string? Summary, JsonElement? Definition);

    private static readonly JsonSerializerOptions ImportJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
}
