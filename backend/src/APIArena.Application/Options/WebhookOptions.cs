using System.ComponentModel.DataAnnotations;

namespace APIArena.Application.Options;

/// <summary>Outbound webhook module (v1). Disabled by default; production additionally requires an attested egress policy.</summary>
public sealed class WebhookOptions
{
    public const string Section = "ApiArena:Webhooks";

    public bool Enabled { get; set; }

    /// <summary>Set only after the network egress restriction in docs/runbooks/EGRESS.md is deployed and verified.</summary>
    public bool EgressRestrictionAttested { get; set; }

    public int[] AllowedPorts { get; set; } = [443, 8443];
    [Range(1, 30)] public int ConnectTimeoutSeconds { get; set; } = 5;
    [Range(1, 60)] public int RequestTimeoutSeconds { get; set; } = 10;
    [Range(1024, 1048576)] public int MaxResponseBytes { get; set; } = 64 * 1024;
    [Range(1, 50)] public int MaxTargetsPerProject { get; set; } = 5;
    [Range(1, 365)] public int VerificationValidityDays { get; set; } = 30;
    [Range(5, 3600)] public int MinSecondsBetweenVerifications { get; set; } = 30;
    [Range(1, 50)] public int DeliveriesPerRun { get; set; } = 10;
    [Range(1, 5)] public int MaxAttemptsPerDelivery { get; set; } = 3;
    [Range(60, 3600)] public int TimestampToleranceSeconds { get; set; } = 300;

    public DevLocalReceiverOptions DevLocalReceiver { get; set; } = new();
    public EncryptionKeyOptions EncryptionKeys { get; set; } = new();
}

/// <summary>
/// Development-only exception that lets the worker reach the sample receiver on localhost over HTTP.
/// ProductionConfigGuard refuses to start if this is enabled in Production.
/// </summary>
public sealed class DevLocalReceiverOptions
{
    public bool Enabled { get; set; }
    public string[] Origins { get; set; } = ["http://localhost:5160"];
}

public sealed class EncryptionKeyOptions
{
    /// <summary>Base64 32-byte AES key. Supplied via environment/secret store, never committed.</summary>
    public string? Current { get; set; }

    public string CurrentId { get; set; } = "k1";

    /// <summary>Older keys still able to decrypt (key rotation): id → base64 key.</summary>
    public Dictionary<string, string> Previous { get; set; } = [];
}
