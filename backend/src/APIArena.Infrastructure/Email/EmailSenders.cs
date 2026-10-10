using System.Text.Json;
using APIArena.Application.Abstractions;
using APIArena.Application.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace APIArena.Infrastructure.Email;

/// <summary>
/// Development-only mail sink: writes each message as a JSON file under a local, git-ignored directory.
/// Never registered in Production (ProductionConfigGuard rejects Mode=File).
/// </summary>
public sealed class FileEmailSender(IOptions<EmailOptions> options, IHostEnvironment environment, TimeProvider clock, ILogger<FileEmailSender> logger) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var directory = options.Value.ResolveFileDirectory(environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            var now = clock.GetUtcNow();
            var id = $"{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var stored = new StoredEmail(id, options.Value.From, message.To, message.Subject, message.TextBody, message.Category, now);
            await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(stored), cancellationToken);
            logger.LogInformation("Development mail sink stored message {MessageId} category {Category}", id, message.Category);
            return EmailSendResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Development mail sink failed for category {Category}", message.Category);
            return EmailSendResult.Fail("mail_sink_unavailable");
        }
    }
}

public sealed record StoredEmail(string Id, string From, string To, string Subject, string TextBody, string Category, DateTimeOffset CreatedAt);

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(o.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(o.SmtpHost ?? throw new InvalidOperationException("SmtpHost missing"), o.SmtpPort, SecureSocketOptions.StartTls, cancellationToken);
            if (!string.IsNullOrEmpty(o.SmtpUsername))
            {
                await client.AuthenticateAsync(o.SmtpUsername, o.SmtpPassword ?? "", cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return EmailSendResult.Ok();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not log the message body: it contains one-time tokens.
            logger.LogError("SMTP delivery failed for category {Category}: {ErrorType}", message.Category, ex.GetType().Name);
            return EmailSendResult.Fail("smtp_failed");
        }
    }
}
