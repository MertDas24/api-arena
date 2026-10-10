using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace APIArena.Domain.Scenarios;

public sealed record CustomBody(string Type, string Content);

public sealed record CustomStep(int Status, int DelayMs, CustomBody? Body, int? RetryAfterSeconds);

/// <summary>
/// Declarative custom scenario, schema version 1 (SEC-06). Data only: fixed responses selected by the per-operation
/// attempt number; attempts after the last step repeat the last step. No scripts, expressions, regex from users,
/// loops, references, outbound URLs or arbitrary headers (only Retry-After).
/// </summary>
public sealed record CustomScenarioDefinition(int SchemaVersion, string Method, string Route, IReadOnlyList<CustomStep> Steps);

public static partial class CustomScenarioCompiler
{
    public const int MaxSteps = 10;
    public const int MaxDelayMs = 5000;
    public const int MaxBodyChars = 4096;
    public const int MaxDocumentBytes = 32 * 1024;

    public static readonly int[] AllowedStatuses = [200, 201, 202, 204, 400, 401, 403, 404, 409, 422, 429, 500, 502, 503, 504];
    public static readonly string[] AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}(/[a-z0-9][a-z0-9-]{0,39}){0,3}$")]
    private static partial Regex RoutePattern();

    public static CustomScenarioDefinition Parse(string json) => ScenarioJson.Deserialize<CustomScenarioDefinition>(json);

    /// <summary>Parses untrusted input (strict: unknown fields rejected) and returns field errors; server-side authority.</summary>
    public static (CustomScenarioDefinition? Definition, Dictionary<string, List<string>> Errors) ParseAndValidate(string json)
    {
        var errors = new Dictionary<string, List<string>>();
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
        {
            Add(errors, "definition", $"Tanım en fazla {MaxDocumentBytes / 1024} KiB olabilir.");
            return (null, errors);
        }

        CustomScenarioDefinition? def;
        try
        {
            def = JsonSerializer.Deserialize<CustomScenarioDefinition>(json, new JsonSerializerOptions(ScenarioJson.Options) { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            Add(errors, "definition", "Geçersiz JSON veya bilinmeyen alan: " + (ex.Path ?? "?"));
            return (null, errors);
        }

        if (def is null)
        {
            Add(errors, "definition", "Tanım boş.");
            return (null, errors);
        }

        Validate(def, errors);
        return (errors.Count == 0 ? def : null, errors);
    }

    public static void Validate(CustomScenarioDefinition def, Dictionary<string, List<string>> errors)
    {
        if (def.SchemaVersion != 1)
        {
            Add(errors, "schemaVersion", "Desteklenen şema sürümü: 1.");
        }

        if (def.Method is null || !AllowedMethods.Contains(def.Method))
        {
            Add(errors, "method", "Metot GET, POST, PUT, PATCH veya DELETE olmalı.");
        }

        if (def.Route is null || !RoutePattern().IsMatch(def.Route))
        {
            Add(errors, "route", "Route küçük harf, rakam ve '-' içeren en fazla 4 segment olmalı (ör. orders/create). '..', '%', boşluk yok.");
        }

        if (def.Steps is null || def.Steps.Count is < 1 or > MaxSteps)
        {
            Add(errors, "steps", $"1 ile {MaxSteps} arasında adım olmalı.");
            return;
        }

        for (var i = 0; i < def.Steps.Count; i++)
        {
            var s = def.Steps[i];
            var p = $"steps[{i}]";
            if (s is null)
            {
                Add(errors, p, "Adım boş olamaz.");
                continue;
            }

            if (!AllowedStatuses.Contains(s.Status))
            {
                Add(errors, $"{p}.status", $"İzin verilen durum kodları: {string.Join(", ", AllowedStatuses)}.");
            }

            if (s.DelayMs is < 0 or > MaxDelayMs)
            {
                Add(errors, $"{p}.delayMs", $"Gecikme 0–{MaxDelayMs} ms olmalı.");
            }

            if (s.RetryAfterSeconds is { } ra && (ra is < 1 or > 60 || s.Status is not (429 or 503)))
            {
                Add(errors, $"{p}.retryAfterSeconds", "Retry-After yalnızca 429/503 için ve 1–60 sn olabilir.");
            }

            if (s.Body is { } body)
            {
                if (body.Type is not ("json" or "text"))
                {
                    Add(errors, $"{p}.body.type", "Gövde tipi 'json' veya 'text' olmalı.");
                }

                if (body.Content is null || body.Content.Length > MaxBodyChars)
                {
                    Add(errors, $"{p}.body.content", $"Gövde en fazla {MaxBodyChars} karakter olabilir.");
                }
                else if (body.Type == "json")
                {
                    try
                    {
                        using var _ = JsonDocument.Parse(body.Content, new JsonDocumentOptions { MaxDepth = 16 });
                    }
                    catch (JsonException)
                    {
                        Add(errors, $"{p}.body.content", "Gövde geçerli JSON değil.");
                    }
                }

                if (s.Status == 204 && !string.IsNullOrEmpty(body.Content))
                {
                    Add(errors, $"{p}.body", "204 yanıtı gövde içeremez.");
                }
            }
        }
    }

    public static SimResponse Decide(CustomScenarioDefinition def, EngineInput input)
    {
        var step = def.Steps[Math.Clamp(input.Attempt, 1, def.Steps.Count) - 1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (step.RetryAfterSeconds is { } ra)
        {
            headers["Retry-After"] = ra.ToString(CultureInfo.InvariantCulture);
        }

        var (body, contentType) = step.Body is null || step.Status == 204
            ? ("", "application/json; charset=utf-8")
            : step.Body.Type == "text"
                ? (step.Body.Content, "text/plain; charset=utf-8")
                : (step.Body.Content, "application/json; charset=utf-8");
        return new SimResponse(step.Status, step.DelayMs, body, headers, contentType);
    }

    /// <summary>Dry run used by the editor preview: exactly the simulator's decision function for attempts 1..N.</summary>
    public static IReadOnlyList<SimResponse> Preview(CustomScenarioDefinition def, int attempts) =>
        Enumerable.Range(1, Math.Clamp(attempts, 1, MaxSteps + 2))
            .Select(a => Decide(def, new EngineInput(a, a, DateTimeOffset.UnixEpoch, null)))
            .ToList();

    private static void Add(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var list))
        {
            errors[key] = list = [];
        }

        list.Add(message);
    }
}
