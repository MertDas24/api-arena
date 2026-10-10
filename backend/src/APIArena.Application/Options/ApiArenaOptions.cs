using System.ComponentModel.DataAnnotations;

namespace APIArena.Application.Options;

public sealed class UrlOptions
{
    public const string Section = "ApiArena:Urls";

    /// <summary>Public origin of the control panel (Next.js). Used for e-mail links and CSRF origin checks.</summary>
    [Required, Url] public string FrontendBaseUrl { get; set; } = "";

    /// <summary>Public base URL of the simulator data plane (separate origin from the control panel).</summary>
    [Required, Url] public string SimulatorPublicBaseUrl { get; set; } = "";

    /// <summary>Extra origins allowed to call the control API with credentials (normally only FrontendBaseUrl).</summary>
    public string[] AdditionalAllowedOrigins { get; set; } = [];

    /// <summary>Reverse proxies whose X-Forwarded-For is trusted (IP addresses).</summary>
    public string[] TrustedProxies { get; set; } = [];
}

public sealed class EmailOptions
{
    public const string Section = "ApiArena:Email";

    /// <summary>File (development sink) or Smtp.</summary>
    [Required] public string Mode { get; set; } = "File";
    public string FileDirectory { get; set; } = ".local/mail";
    [Required, EmailAddress] public string From { get; set; } = "no-reply@apiarena.local";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }

    public string ResolveFileDirectory(string contentRoot) => Path.GetFullPath(Path.Combine(contentRoot, FileDirectory));
}

public sealed class LimitOptions
{
    public const string Section = "ApiArena:Limits";

    [Range(1, 50)] public int ActiveRunsPerUser { get; set; } = 3;
    [Range(1, 10000)] public int DailyRunsPerUser { get; set; } = 30;
    [Range(1, 10000)] public int RequestsPerRun { get; set; } = 200;
    [Range(1, 120)] public int RunTtlMinutes { get; set; } = 5;
    [Range(0, 5000)] public int MaxDelayMs { get; set; } = 5000;
    [Range(1024, 1048576)] public int MaxRequestBodyBytes { get; set; } = 64 * 1024;
    [Range(1024, 1048576)] public int MaxResponseBodyBytes { get; set; } = 64 * 1024;
    [Range(256, 8192)] public int MaxUrlLength { get; set; } = 2048;
    [Range(4096, 65536)] public int MaxRequestHeadersBytes { get; set; } = 16 * 1024;
    [Range(64, 8192)] public int BodyPreviewChars { get; set; } = 2048;
    [Range(1, 1000)] public int ProjectsPerUser { get; set; } = 50;
    [Range(1, 1000)] public int MaxExportObservations { get; set; } = 500;
    [Range(5, 1440)] public int RunKeyTtlMinutes { get; set; } = 60;
    [Range(1, 100)] public int MaxKeysPerRun { get; set; } = 20;
    [Range(10, 100000)] public int SimulatorRequestsPerIpPerMinute { get; set; } = 1200;
}

public sealed class AuthOptions
{
    public const string Section = "ApiArena:Auth";

    public bool RequireVerifiedEmailForRuns { get; set; } = true;
    [Range(5, 1440)] public int SessionIdleMinutes { get; set; } = 480;
    [Range(1, 1000)] public int AuthPermitsPerMinute { get; set; } = 10;
    [Range(1, 1000)] public int SensitivePermitsPer15Minutes { get; set; } = 5;
    [Range(5, 1440)] public int PasswordResetTokenMinutes { get; set; } = 30;
    [Range(60, 4320)] public int EmailConfirmationTokenMinutes { get; set; } = 1440;
}
