using System.Security.Cryptography;
using APIArena.Application.Options;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure.Outbound;

public interface ISecretProtector
{
    (byte[] Ciphertext, string KeyId) Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext, string keyId);

    bool IsConfigured { get; }
}

/// <summary>
/// AES-256-GCM encryption of webhook signing secrets at rest (WH-06). Keys come from configuration (environment /
/// secret store), never from the database or repository. Format: nonce(12) | tag(16) | ciphertext. A secret needed
/// for HMAC cannot be stored as a hash, so encryption (not hashing) is required. Rotation: add the new key as Current,
/// keep old ones under Previous until re-encryption completes (docs/runbooks/KEYS.md).
/// </summary>
public sealed class AesGcmSecretProtector(IOptions<WebhookOptions> options) : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public bool IsConfigured => TryKey(options.Value.EncryptionKeys.CurrentId, out _);

    public (byte[] Ciphertext, string KeyId) Protect(byte[] plaintext)
    {
        var keyId = options.Value.EncryptionKeys.CurrentId;
        if (!TryKey(keyId, out var key))
        {
            throw new InvalidOperationException("Webhook encryption key is not configured (ApiArena:Webhooks:EncryptionKeys:Current).");
        }

        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), associatedData: System.Text.Encoding.UTF8.GetBytes(keyId));
        return (output, keyId);
    }

    public byte[] Unprotect(byte[] ciphertext, string keyId)
    {
        if (!TryKey(keyId, out var key))
        {
            throw new CryptographicException("Unknown encryption key id.");
        }

        var plaintext = new byte[ciphertext.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(ciphertext.AsSpan(0, NonceSize), ciphertext.AsSpan(NonceSize + TagSize), ciphertext.AsSpan(NonceSize, TagSize), plaintext,
            associatedData: System.Text.Encoding.UTF8.GetBytes(keyId));
        return plaintext;
    }

    private bool TryKey(string keyId, out byte[] key)
    {
        var o = options.Value.EncryptionKeys;
        var b64 = keyId == o.CurrentId ? o.Current : o.Previous.GetValueOrDefault(keyId);
        key = [];
        if (string.IsNullOrWhiteSpace(b64))
        {
            return false;
        }

        try
        {
            key = Convert.FromBase64String(b64);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
