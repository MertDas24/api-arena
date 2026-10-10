using System.Security.Cryptography;
using System.Text;

namespace APIArena.Domain.Outbound;

/// <summary>
/// Canonical webhook signature (WH-08), documented in docs/WEBHOOKS.md:
///   X-Arena-Signature: v1=hex(HMAC-SHA256(secret, "{timestamp}.{deliveryId}.{rawBody}"))
/// The timestamp (unix seconds) and delivery id are bound into the MAC so they cannot be swapped; receivers reject
/// timestamps outside a tolerance window and de-duplicate delivery ids (replay) and event ids (business idempotency).
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-Arena-Signature";
    public const string TimestampHeader = "X-Arena-Timestamp";
    public const string EventIdHeader = "X-Arena-Event-Id";
    public const string DeliveryIdHeader = "X-Arena-Delivery-Id";
    public const string RunIdHeader = "X-Arena-Run-Id";

    public static string Compute(byte[] secret, long timestamp, string deliveryId, string body) =>
        "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{timestamp}.{deliveryId}.{body}")));

    public static bool Verify(byte[] secret, string? header, long timestamp, string deliveryId, string body, DateTimeOffset now, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(header) || Math.Abs(now.ToUnixTimeSeconds() - timestamp) > toleranceSeconds)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, deliveryId, body));
        var actual = Encoding.ASCII.GetBytes(header);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Ownership proof: only someone holding the signing secret can answer the random challenge.</summary>
    public static string ChallengeResponse(byte[] secret, string challenge) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("apiarena-verify." + challenge)));

    public static bool VerifyChallenge(byte[] secret, string challenge, string? response) =>
        response is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ChallengeResponse(secret, challenge)), Encoding.ASCII.GetBytes(response));

    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return "whsec_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] SecretBytes(string secret) => Encoding.UTF8.GetBytes(secret);
}
