namespace APIArena.Application.Common;

/// <summary>Base class for expected, user-facing failures. Mapped to RFC 9457 ProblemDetails by the hosts.</summary>
public abstract class AppException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>Also used for resources owned by someone else, so existence is not disclosed (IDOR).</summary>
public sealed class NotFoundException(string resource) : AppException(404, "not_found", $"{resource} bulunamadı.");

public sealed class ConflictException(string code, string message) : AppException(409, code, message);

public sealed class ForbiddenException(string code, string message) : AppException(403, code, message);

public sealed class QuotaExceededException(string code, string message) : AppException(429, code, message);

public sealed class ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
    : AppException(400, "validation_failed", "Gönderilen veriler geçersiz.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public static ValidationFailedException For(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
