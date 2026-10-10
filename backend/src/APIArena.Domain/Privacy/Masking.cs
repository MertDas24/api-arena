using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace APIArena.Domain.Privacy;

/// <summary>
/// Data minimisation for recorded traffic (RUN-08, C5 "Veri ve uygulama"):
/// - only allow-listed header values are kept; credentials are replaced by [REDACTED]; everything else is name-only;
/// - body previews are OFF unless the project opts in, and even then sensitive JSON keys and token-like strings are masked.
/// Unknown fields can still contain personal data — the UI states this when body capture is enabled.
/// </summary>
public static partial class Masking
{
    public const string Redacted = "[REDACTED]";
    public const string NotRecorded = "[not recorded]";
    public const int MaxHeaders = 30;
    public const int MaxHeaderValue = 200;

    // Readable previews (no \u003C escaping). Safe because previews are only ever rendered as plain text.
    private static readonly JsonSerializerOptions PreviewJson = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> RecordedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "content-type", "content-length", "user-agent", "accept", "accept-encoding", "idempotency-key",
        "x-request-id", "traceparent", "origin", "retry-attempt", "x-retry-attempt",
    };

    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key", "x-auth-token", "x-csrf-token",
    };

    [GeneratedRegex("(pass|secret|token|auth|key|card|cvv|cvc|iban|ssn|session|cookie|pin|credential)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKey();

    [GeneratedRegex(@"aa_(rk|sh)_[A-Za-z0-9_-]{10,}|Bearer\s+[A-Za-z0-9._~+/=-]{8,}|\b\d{13,19}\b|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,}")]
    private static partial Regex SensitiveValue();

    public static string HeadersJson(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in headers.Take(MaxHeaders))
        {
            var key = name.ToLowerInvariant();
            if (obj.ContainsKey(key))
            {
                continue;
            }

            obj[key] = CredentialHeaders.Contains(key) ? Redacted
                : RecordedHeaders.Contains(key) ? MaskString(Truncate(value, MaxHeaderValue))
                : NotRecorded;
        }

        return obj.ToJsonString();
    }

    public static (string Preview, bool Truncated) BodyPreview(byte[] body, int maxChars)
    {
        var text = Encoding.UTF8.GetString(body);
        string masked;
        try
        {
            var node = JsonNode.Parse(text);
            masked = node is null ? "null" : MaskNode(node).ToJsonString(PreviewJson);
        }
        catch (JsonException)
        {
            masked = MaskString(text);
        }

        return masked.Length > maxChars ? (masked[..maxChars], true) : (masked, false);
    }

    public static string MaskString(string value) => SensitiveValue().Replace(value, "***");

    private static JsonNode MaskNode(JsonNode node, int depth = 0)
    {
        if (depth > 16)
        {
            return JsonValue.Create("***")!;
        }

        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (SensitiveKey().IsMatch(key))
                    {
                        o[key] = "***";
                    }
                    else if (o[key] is { } child)
                    {
                        o[key] = MaskNode(child.DeepClone(), depth + 1);
                    }
                }

                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is { } child)
                    {
                        a[i] = MaskNode(child.DeepClone(), depth + 1);
                    }
                }

                return a;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(MaskString(s))!;
            default:
                return node;
        }
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "…" : s;
}
