using APIArena.Api;
using APIArena.Simulator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace APIArena.IntegrationTests.Infrastructure;

public static class TestSettings
{
    public static Dictionary<string, string?> Base(string connectionString, string mailDirectory) => new()
    {
        ["ConnectionStrings:ApiArena"] = connectionString,
        ["ApiArena:Urls:FrontendBaseUrl"] = "http://localhost",
        ["ApiArena:Urls:SimulatorPublicBaseUrl"] = "http://sim.localhost",
        ["ApiArena:Email:Mode"] = "File",
        ["ApiArena:Email:FileDirectory"] = mailDirectory,
        ["ApiArena:Auth:AuthPermitsPerMinute"] = "1000",
        ["ApiArena:Auth:SensitivePermitsPer15Minutes"] = "1000",
    };
}

/// <summary>Control API host on the shared test database. Uses a controllable clock.</summary>
/// <param name="start">Fake clock start. Tests that run the global expiry sweep use an earlier date so they cannot expire other tests' runs in the shared DB.</param>
public class ApiFactory(SqlDatabaseFixture db, DateTimeOffset? start = null) : WebApplicationFactory<ApiMarker>
{
    public FakeTimeProvider Clock { get; } = new(start ?? new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    public string MailDirectory { get; } = Path.Combine(Path.GetTempPath(), "apiarena-tests-mail", Guid.NewGuid().ToString("N"));
    public CapturingLoggerProvider Logs { get; } = new();
    public Dictionary<string, string?> Overrides { get; } = new();
    public bool UseRealClock { get; init; }
    public string Environment { get; set; } = "Testing";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });
        var settings = TestSettings.Base(db.ConnectionString, MailDirectory);
        foreach (var (k, v) in Overrides)
        {
            settings[k] = v;
        }

        // UseSetting (not ConfigureAppConfiguration) so values are visible while Program.cs builds services.
        foreach (var (k, v) in settings)
        {
            builder.UseSetting(k, v);
        }

        builder.ConfigureLogging(l => l.AddProvider(Logs));
        builder.ConfigureServices(s =>
        {
            if (!UseRealClock)
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(Clock);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(MailDirectory))
            {
                Directory.Delete(MailDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Simulator host sharing the same database and (optionally) the same fake clock as an <see cref="ApiFactory"/>.</summary>
public class SimulatorFactory(SqlDatabaseFixture db, TimeProvider clock) : WebApplicationFactory<SimulatorMarker>
{
    public CapturingLoggerProvider Logs { get; } = new();
    public Dictionary<string, string?> Overrides { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });
        var settings = TestSettings.Base(db.ConnectionString, Path.GetTempPath());
        foreach (var (k, v) in Overrides)
        {
            settings[k] = v;
        }

        foreach (var (k, v) in settings)
        {
            builder.UseSetting(k, v);
        }

        builder.ConfigureLogging(l => l.AddProvider(Logs));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton(clock);
        });
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) { return _lines.ToList(); } } }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel} {category}: {formatter(state, exception)} {exception}";
            lock (owner._lines)
            {
                owner._lines.Add(line);
            }
        }
    }
}
