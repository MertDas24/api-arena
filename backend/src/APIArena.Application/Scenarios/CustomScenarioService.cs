using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Access;
using APIArena.Application.Common;
using APIArena.Domain.Entities;
using APIArena.Domain.Scenarios;
using Microsoft.EntityFrameworkCore;

namespace APIArena.Application.Scenarios;

public sealed record CustomScenarioRequest(string? Title, string? Summary, JsonElement? Definition);

public sealed record PreviewRequest(JsonElement? Definition, int? Attempts);

public sealed record PreviewStep(int Attempt, int Status, int DelayMs, string ContentType, string Body, string? RetryAfter);

/// <summary>
/// Declarative custom scenarios (SEC-06). The server is the authority: definitions are parsed strictly (unknown fields
/// rejected), validated against hard limits, and every edit creates a new immutable version so old runs/reports never
/// change. Templates are private to their owner.
/// </summary>
public sealed class CustomScenarioService(IAppDbContext db, AccessScope scope, TimeProvider clock)
{
    public const int MaxTemplatesPerUser = 20;
    public const int MaxVersions = 50;
    public const string EvaluatorVersion = "custom@1.0";

    public IReadOnlyList<PreviewStep> Preview(PreviewRequest request)
    {
        var def = Parse(request.Definition);
        return CustomScenarioCompiler.Preview(def, request.Attempts ?? def.Steps.Count + 1)
            .Select((r, i) => new PreviewStep(i + 1, r.Status, r.DelayMs, r.ContentType, r.Body, r.Headers.GetValueOrDefault("Retry-After")))
            .ToList();
    }

    public async Task<ScenarioDto> CreateAsync(Guid userId, CustomScenarioRequest request, CancellationToken ct)
    {
        var (title, summary) = ValidateMeta(request.Title, request.Summary);
        var def = Parse(request.Definition);
        if (await db.ScenarioTemplates.CountAsync(t => t.OwnerId == userId && !t.IsArchived, ct) >= MaxTemplatesPerUser)
        {
            throw new QuotaExceededException("quota_custom_scenarios", $"En fazla {MaxTemplatesPerUser} özel senaryo oluşturabilirsiniz.");
        }

        var now = clock.GetUtcNow();
        var template = new ScenarioTemplate
        {
            Id = Guid.NewGuid(),
            Slug = "custom-" + Guid.NewGuid().ToString("N")[..10],
            Kind = ScenarioKind.Custom,
            OwnerId = userId,
            Title = title,
            Summary = summary,
            CreatedAt = now,
        };
        db.ScenarioTemplates.Add(template);
        db.ScenarioVersions.Add(NewVersion(template.Id, 1, def, summary, now));
        await db.SaveChangesAsync(ct);
        return await GetOwnAsync(userId, template.Id, ct);
    }

    /// <summary>Edits never modify an existing version: a new version row is appended.</summary>
    public async Task<ScenarioDto> AddVersionAsync(Guid userId, Guid templateId, CustomScenarioRequest request, CancellationToken ct)
    {
        var template = await RequireOwnAsync(userId, templateId, ct);
        var (title, summary) = ValidateMeta(request.Title ?? template.Title, request.Summary ?? template.Summary);
        var def = Parse(request.Definition);
        var next = template.Versions.Max(v => v.Version) + 1;
        if (next > MaxVersions)
        {
            throw new QuotaExceededException("quota_custom_versions", $"Bir senaryoda en fazla {MaxVersions} sürüm olabilir.");
        }

        template.Title = title;
        template.Summary = summary;
        db.ScenarioVersions.Add(NewVersion(template.Id, next, def, summary, clock.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("concurrent_version", "Aynı anda başka bir sürüm kaydedildi; sayfayı yenileyin.");
        }

        return await GetOwnAsync(userId, templateId, ct);
    }

    public async Task ArchiveAsync(Guid userId, Guid templateId, CancellationToken ct)
    {
        var template = await RequireOwnAsync(userId, templateId, ct);
        template.IsArchived = true; // runs keep referencing their immutable versions
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> ExportAsync(Guid userId, Guid templateId, int? version, CancellationToken ct)
    {
        var template = await RequireOwnAsync(userId, templateId, ct);
        var v = version is { } n ? template.Versions.FirstOrDefault(x => x.Version == n) ?? throw new NotFoundException("Sürüm") : template.Versions.MaxBy(x => x.Version)!;
        return new { format = "apiarena.custom-scenario", title = template.Title, summary = template.Summary, definition = JsonDocument.Parse(v.DefinitionJson).RootElement };
    }

    public async Task<ScenarioDto> GetOwnAsync(Guid userId, Guid templateId, CancellationToken ct) =>
        ScenarioCatalog.ToDto(await RequireOwnAsync(userId, templateId, ct), userId);

    private async Task<ScenarioTemplate> RequireOwnAsync(Guid userId, Guid templateId, CancellationToken ct) =>
        await scope.VisibleTemplates(userId).Include(t => t.Versions)
            .FirstOrDefaultAsync(t => t.Id == templateId && t.OwnerId == userId && t.Kind == ScenarioKind.Custom, ct)
        ?? throw new NotFoundException("Özel senaryo");

    private static CustomScenarioDefinition Parse(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e)
        {
            throw ValidationFailedException.For("definition", "Senaryo tanımı (JSON nesnesi) gerekli.");
        }

        var (def, errors) = CustomScenarioCompiler.ParseAndValidate(e.GetRawText());
        if (def is null)
        {
            throw new ValidationFailedException(errors.ToDictionary(k => "definition." + k.Key, k => k.Value.ToArray()));
        }

        return def;
    }

    private static (string Title, string Summary) ValidateMeta(string? title, string? summary)
    {
        var t = title?.Trim() ?? "";
        var s = summary?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (t.Length is < 3 or > 120 || t.Any(char.IsControl))
        {
            errors["title"] = ["Başlık 3–120 karakter olmalı."];
        }

        if (s.Length > 500 || s.Any(c => char.IsControl(c) && c != '\n'))
        {
            errors["summary"] = ["Özet en fazla 500 karakter olabilir."];
        }

        return errors.Count > 0 ? throw new ValidationFailedException(errors) : (t, s);
    }

    private static ScenarioVersion NewVersion(Guid templateId, int version, CustomScenarioDefinition def, string summary, DateTimeOffset now)
    {
        var definitionJson = ScenarioJson.Serialize(def);
        var hasRetryAfter = def.Steps.Any(s => s.RetryAfterSeconds is not null);
        var content = new ScenarioContent(
            string.IsNullOrEmpty(summary) ? "Kullanıcı tanımlı deklaratif senaryo." : summary,
            "Kendi tanımladığın hata dizisine istemcinin nasıl tepki verdiğini gözlemek.",
            [$"İstemcinde adresi SIM_BASE_URL + /{def.Route} yap ({def.Method}).", "Authorization: Bearer <run-key> ekle; aynı işlem için sabit Idempotency-Key kullan."],
            def.Steps.Select((s, i) => $"Deneme {i + 1} → {s.Status}{(s.DelayMs > 0 ? $" ({s.DelayMs} ms gecikme)" : "")}{(s.RetryAfterSeconds is { } ra ? $", Retry-After: {ra}" : "")}")
                .Append("Sonraki denemeler son adımı tekrarlar").ToList(),
            [
                "all-steps-exercised: bir işlem tüm adımlara ulaşmalı.",
                hasRetryAfter ? "retry-after-respected: Retry-After içeren yanıttan sonra en az o kadar beklenmeli." : "retry-after-respected: senaryoda Retry-After yok → NotApplicable.",
                "business-outcome: her zaman Inconclusive (HTTP'den gözlenemez).",
            ],
            ["Uygulamanın iş sonucu ve kullanıcıya gösterilen mesaj HTTP trafiğinden görülemez; özel senaryolarda ek kriterler değerlendirilmez."],
            [$"En fazla {CustomScenarioCompiler.MaxSteps} adım", $"Adım başına en fazla {CustomScenarioCompiler.MaxDelayMs} ms gecikme", "Koşu başına 200 istek, 5 dk"],
            $"curl -i -X {def.Method} \"$SIM_BASE_URL/{def.Route}\" -H \"Authorization: Bearer $RUN_KEY\" -H \"Idempotency-Key: demo-1\"",
            "Koşuyu başlattıktan sonra sahte servis adresini ve anahtarı uygulamanın ortam değişkenlerine koy.");
        var contentJson = ScenarioJson.Serialize(content);
        return new ScenarioVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = templateId,
            Version = version,
            Engine = Engines.Custom,
            EvaluatorVersion = EvaluatorVersion,
            DefinitionJson = definitionJson,
            ContentJson = contentJson,
            ContentHash = ScenarioCatalog.Hash(definitionJson, contentJson, Engines.Custom, EvaluatorVersion),
            CreatedAt = now,
        };
    }
}
