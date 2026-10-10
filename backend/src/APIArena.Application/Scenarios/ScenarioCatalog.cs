using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Domain.Entities;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace APIArena.Application.Scenarios;

public sealed record ScenarioVersionDto(Guid Id, int Version, string Engine, string EvaluatorVersion, JsonElement Definition, ScenarioContent Content, DateTimeOffset CreatedAt);

public sealed record ScenarioDto(Guid Id, string Slug, string Kind, string Title, string Summary, bool IsOwn, ScenarioVersionDto Latest, IReadOnlyList<int> Versions);

public sealed class ScenarioCatalog(IAppDbContext db, AccessScope scope, Microsoft.Extensions.Options.IOptions<APIArena.Application.Options.WebhookOptions> webhooks)
{
    public async Task<IReadOnlyList<ScenarioDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var templates = await scope.VisibleTemplates(userId).Include(t => t.Versions).AsNoTracking()
            .OrderBy(t => t.Kind).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        // Webhook scenarios are only listed when the (default-off) webhook module is enabled.
        return templates.Where(t => t.Versions.Count > 0 && (webhooks.Value.Enabled || !Engines.IsWebhook(t.Versions.OrderByDescending(v => v.Version).First().Engine)))
            .Select(t => ToDto(t, userId)).ToList();
    }

    public async Task<ScenarioDto> GetAsync(Guid userId, string slugOrId, CancellationToken ct) =>
        ToDto(await scope.RequireTemplateAsync(userId, slugOrId, ct), userId);

    public async Task<ScenarioVersionDto> GetVersionAsync(Guid userId, string slugOrId, int version, CancellationToken ct)
    {
        var template = await scope.RequireTemplateAsync(userId, slugOrId, ct);
        var v = template.Versions.FirstOrDefault(x => x.Version == version) ?? throw new Common.NotFoundException("Senaryo sürümü");
        return ToVersionDto(v);
    }

    public static ScenarioDto ToDto(ScenarioTemplate t, Guid userId)
    {
        var latest = t.Versions.OrderByDescending(v => v.Version).First();
        return new ScenarioDto(t.Id, t.Slug, t.Kind.ToString(), t.Title, t.Summary, t.OwnerId == userId, ToVersionDto(latest),
            t.Versions.Select(v => v.Version).OrderBy(v => v).ToList());
    }

    public static ScenarioVersionDto ToVersionDto(ScenarioVersion v) =>
        new(v.Id, v.Version, v.Engine, v.EvaluatorVersion, JsonDocument.Parse(v.DefinitionJson).RootElement.Clone(),
            JsonSerializer.Deserialize<ScenarioContent>(v.ContentJson, ScenarioJson.Options)!, v.CreatedAt);

    public static string Hash(string definitionJson, string contentJson, string engine, string evaluator) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{engine}\n{evaluator}\n{definitionJson}\n{contentJson}")));
}

/// <summary>
/// Idempotently inserts built-in (slug, version) rows. Never updates an existing version: if code and DB disagree,
/// it logs a warning and keeps the stored (immutable) row — a behaviour change must ship as a new version.
/// </summary>
public sealed class ScenarioSeeder(IAppDbContext db, TimeProvider clock, ILogger<ScenarioSeeder> logger)
{
    public async Task SeedAsync(IEnumerable<BuiltInScenario> scenarios, CancellationToken ct)
    {
        foreach (var s in scenarios)
        {
            var definitionJson = ScenarioJson.Serialize(s.Definition);
            var contentJson = ScenarioJson.Serialize(s.Content);
            var hash = ScenarioCatalog.Hash(definitionJson, contentJson, s.Engine, s.EvaluatorVersion);

            var template = await db.ScenarioTemplates.Include(t => t.Versions)
                .FirstOrDefaultAsync(t => t.Slug == s.Slug && t.OwnerId == null, ct);
            if (template is null)
            {
                template = new ScenarioTemplate
                {
                    Id = Guid.NewGuid(),
                    Slug = s.Slug,
                    Kind = ScenarioKind.BuiltIn,
                    Title = s.Title,
                    Summary = s.Summary,
                    CreatedAt = clock.GetUtcNow(),
                };
                db.ScenarioTemplates.Add(template);
            }

            var existing = template.Versions.FirstOrDefault(v => v.Version == s.Version);
            if (existing is not null)
            {
                if (existing.ContentHash != hash)
                {
                    logger.LogWarning("Built-in scenario {Slug} v{Version} differs from stored immutable version; stored row kept. Publish a new version instead.", s.Slug, s.Version);
                }

                continue;
            }

            template.Title = s.Title;
            template.Summary = s.Summary;
            db.ScenarioVersions.Add(new ScenarioVersion
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                Version = s.Version,
                Engine = s.Engine,
                EvaluatorVersion = s.EvaluatorVersion,
                DefinitionJson = definitionJson,
                ContentJson = contentJson,
                ContentHash = hash,
                CreatedAt = clock.GetUtcNow(),
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another instance seeded concurrently; unique indexes guarantee a single row.
                logger.LogInformation("Scenario {Slug} v{Version} seeded concurrently by another instance.", s.Slug, s.Version);
                foreach (var entry in ((DbContext)db).ChangeTracker.Entries().ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }
}
