using System.Security.Cryptography;
using System.Text;
using APIArena.Domain.Entities;

namespace APIArena.Domain.Runs;

public static class RunStateMachine
{
    public static bool IsTerminal(RunStatus s) => s is RunStatus.Completed or RunStatus.Cancelled or RunStatus.Expired or RunStatus.Failed;

    public static bool IsActive(RunStatus s) => s is RunStatus.Created or RunStatus.Running;

    /// <summary>Created → Running → Completed | Cancelled | Expired | Failed; Created → Cancelled | Failed.</summary>
    public static bool CanTransition(RunStatus from, RunStatus to) => (from, to) switch
    {
        (RunStatus.Created, RunStatus.Running) => true,
        (RunStatus.Created, RunStatus.Cancelled) => true,
        (RunStatus.Created, RunStatus.Failed) => true,
        (RunStatus.Running, RunStatus.Completed) => true,
        (RunStatus.Running, RunStatus.Cancelled) => true,
        (RunStatus.Running, RunStatus.Expired) => true,
        (RunStatus.Running, RunStatus.Failed) => true,
        _ => false,
    };
}

public static class IncompleteReasons
{
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string QuotaExceeded = "quota_exceeded";
    public const string PlatformFailure = "platform_failure";
    public const string ProjectDeleted = "project_deleted";
    public const string AccountDeleted = "account_deleted";
}

/// <summary>Run access keys (RUN-04): 256-bit CSPRNG, shown once, only the SHA-256 hash is stored.</summary>
public static class RunKeys
{
    public const string Prefix = "aa_rk_";
    public const int RandomBytes = 32;

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[RandomBytes];
        RandomNumberGenerator.Fill(bytes);
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Cheap shape check done before any DB work, so malformed credentials never cost a query.</summary>
    public static bool LooksValid(string? key) =>
        key is { Length: 49 } && key.StartsWith(Prefix, StringComparison.Ordinal)
        && key.AsSpan(Prefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    public static string DisplayPrefix(string key) => key[..10];
}

/// <summary>Opaque public tokens (share links) — same generation and hashing scheme as run keys.</summary>
public static class OpaqueTokens
{
    public static string Generate(string prefix)
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
