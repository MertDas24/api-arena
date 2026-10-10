namespace APIArena.Application.Abstractions;

public sealed record EmailMessage(string To, string Subject, string TextBody, string Category);

public sealed record EmailSendResult(bool Sent, string? FailureReason)
{
    public static EmailSendResult Ok() => new(true, null);
    public static EmailSendResult Fail(string reason) => new(false, reason);
}

/// <summary>Sends transactional email. Implementations must never log the message body (it may contain one-time tokens).</summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
