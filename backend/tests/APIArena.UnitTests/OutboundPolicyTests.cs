using System.Net;
using System.Security.Cryptography;
using APIArena.Application.Options;
using APIArena.Domain.Outbound;
using APIArena.Infrastructure.Outbound;
using Microsoft.Extensions.Options;

namespace APIArena.UnitTests;

public sealed class OutboundPolicyTests
{
    private static readonly int[] Ports = [443, 8443];

    [Theory]
    [InlineData("https://hooks.example-company.com/apiarena", null)]
    [InlineData("https://hooks.example-company.com:8443/x?y=1", null)]
    [InlineData("https://xn--bcher-kva.de/hook", null)]
    [InlineData("http://hooks.example-company.com/x", "url_https_required")]
    [InlineData("https://user:pw@hooks.example-company.com/", "url_userinfo_not_allowed")]
    [InlineData("https://hooks.example-company.com@evil.com/", "url_userinfo_not_allowed")]
    [InlineData("https://hooks.example-company.com/#frag", "url_fragment_not_allowed")]
    [InlineData("https://hooks.example-company.com:22/", "url_port_not_allowed")]
    [InlineData("https://127.0.0.1/", "url_ip_literal_not_allowed")]
    [InlineData("https://[::1]/", "url_ip_literal_not_allowed")]
    [InlineData("https://[::ffff:127.0.0.1]/", "url_ip_literal_not_allowed")]
    [InlineData("https://2130706433/", "url_ip_literal_not_allowed")]
    [InlineData("https://0x7f000001/", "url_ip_literal_not_allowed")]
    [InlineData("https://0x7f.1/", "url_ip_literal_not_allowed")]
    [InlineData("https://017700000001/", "url_ip_literal_not_allowed")]
    [InlineData("https://127.1/", "url_ip_literal_not_allowed")]
    [InlineData("https://localhost/", "url_host_not_allowed")]
    [InlineData("https://printer.local/", "url_host_not_allowed")]
    [InlineData("https://db.internal/", "url_host_not_allowed")]
    [InlineData("https://metadata.google.internal/", "url_host_not_allowed")]
    [InlineData("https://intranet/", "url_host_invalid")]
    [InlineData("https://hooks.example-company.com/a b", "url_control_characters")]
    [InlineData("https://hooks.example-company.com/\u0000", "url_control_characters")]
    [InlineData("https://hooks.example-company.com\\@evil.com/", "url_control_characters")]
    [InlineData("ftp://hooks.example-company.com/", "url_https_required")]
    [InlineData("//hooks.example-company.com/", "*")]
    [InlineData("", "url_invalid")]
    public void Url_policy(string url, string? expectedError)
    {
        var (target, error) = UrlPolicy.Validate(url, Ports);
        if (expectedError == "*")
        {
            Assert.NotNull(error); // rejected; the exact code differs by platform URI parsing
            Assert.Null(target);
            return;
        }

        Assert.Equal(expectedError, error);
        Assert.Equal(expectedError is null, target is not null);
    }

    [Fact]
    public void Dev_local_exception_is_exact_origin_only()
    {
        string[] dev = ["http://localhost:5160"];
        Assert.True(UrlPolicy.Validate("http://localhost:5160/webhooks/apiarena", Ports, dev).Target!.IsDevLocalException);
        Assert.Equal("url_https_required", UrlPolicy.Validate("http://localhost:5161/x", Ports, dev).Error);
        Assert.Equal("url_https_required", UrlPolicy.Validate("http://127.0.0.1:5160/x", Ports, dev).Error);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("ff02::1")]
    public void Non_public_addresses_are_rejected(string ip) => Assert.False(IpPolicy.IsPubliclyRoutable(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_pass(string ip) => Assert.True(IpPolicy.IsPubliclyRoutable(IPAddress.Parse(ip)));

    [Fact]
    public void One_private_answer_in_a_mixed_dns_response_rejects_the_target()
    {
        Assert.False(IpPolicy.AllPubliclyRoutable([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")]));
        Assert.False(IpPolicy.AllPubliclyRoutable([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("::1")]));
        Assert.False(IpPolicy.AllPubliclyRoutable([]));
    }

    [Fact]
    public void Signature_binds_timestamp_delivery_and_body()
    {
        var secret = WebhookSignature.SecretBytes(WebhookSignature.NewSecret());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var ts = now.ToUnixTimeSeconds();
        var sig = WebhookSignature.Compute(secret, ts, "d1", "{\"a\":1}");
        Assert.True(WebhookSignature.Verify(secret, sig, ts, "d1", "{\"a\":1}", now, 300));
        Assert.False(WebhookSignature.Verify(secret, sig, ts, "d1", "{\"a\":2}", now, 300));
        Assert.False(WebhookSignature.Verify(secret, sig, ts, "d2", "{\"a\":1}", now, 300));
        Assert.False(WebhookSignature.Verify(secret, sig, ts, "d1", "{\"a\":1}", now.AddSeconds(301), 300));
        Assert.False(WebhookSignature.Verify(secret, sig, ts + 1, "d1", "{\"a\":1}", now, 300));
        Assert.False(WebhookSignature.Verify(WebhookSignature.SecretBytes("other"), sig, ts, "d1", "{\"a\":1}", now, 300));
        Assert.True(WebhookSignature.VerifyChallenge(secret, "c", WebhookSignature.ChallengeResponse(secret, "c")));
        Assert.False(WebhookSignature.VerifyChallenge(secret, "c", "c"));
    }

    [Fact]
    public void Aes_gcm_protects_and_supports_rotation()
    {
        static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var oldKey = Key();
        var o = new WebhookOptions { EncryptionKeys = new EncryptionKeyOptions { Current = oldKey, CurrentId = "k1" } };
        var p1 = new AesGcmSecretProtector(Microsoft.Extensions.Options.Options.Create(o));
        var (cipher, keyId) = p1.Protect("whsec_abc"u8.ToArray());
        Assert.Equal("k1", keyId);
        Assert.DoesNotContain("whsec_abc", System.Text.Encoding.UTF8.GetString(cipher));
        Assert.Equal("whsec_abc"u8.ToArray(), p1.Unprotect(cipher, keyId));

        var tampered = (byte[])cipher.Clone();
        tampered[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => p1.Unprotect(tampered, keyId));
        Assert.ThrowsAny<CryptographicException>(() => p1.Unprotect(cipher, "unknown"));

        // Rotation: new current key, old key kept under Previous still decrypts existing ciphertexts.
        var rotated = new WebhookOptions { EncryptionKeys = new EncryptionKeyOptions { Current = Key(), CurrentId = "k2", Previous = { ["k1"] = oldKey } } };
        var p2 = new AesGcmSecretProtector(Microsoft.Extensions.Options.Options.Create(rotated));
        Assert.Equal("whsec_abc"u8.ToArray(), p2.Unprotect(cipher, "k1"));
        Assert.Equal("k2", p2.Protect([1, 2, 3]).KeyId);
    }
}
