using System.Text;
using APIArena.Application.Options;
using APIArena.Application.Webhooks;
using APIArena.Domain.Outbound;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure.Outbound;

internal sealed class WebhookUrlValidator(IOptions<WebhookOptions> options, IHostEnvironment env) : IWebhookUrlValidator
{
    public (ValidatedTarget? Target, string? Error) Validate(string? url)
    {
        var policy = SafeHttpClient.DefaultPolicy(options.Value, env);
        return UrlPolicy.Validate(url, policy.AllowedPorts, policy.DevLocalOrigins);
    }
}

internal sealed class SigningSecretStore(ISecretProtector protector) : ISigningSecretStore
{
    public (byte[] Ciphertext, string KeyId) Protect(string secret) => protector.Protect(Encoding.UTF8.GetBytes(secret));

    public string Reveal(byte[] ciphertext, string keyId) => Encoding.UTF8.GetString(protector.Unprotect(ciphertext, keyId));
}
