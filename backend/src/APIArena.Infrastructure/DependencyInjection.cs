using APIArena.Application;
using APIArena.Application.Abstractions;
using APIArena.Infrastructure.Email;
using APIArena.Application.Options;
using APIArena.Infrastructure.Options;
using APIArena.Infrastructure.Persistence;
using APIArena.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace APIArena.Infrastructure;

public static partial class DependencyInjection
{
    public const string ReadyTag = "ready";

    /// <summary>Services shared by the control API, simulator and worker hosts.</summary>
    public static IServiceCollection AddApiArenaCore(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ProductionConfigGuard.ThrowIfInvalid(configuration, environment);

        services.AddOptions<UrlOptions>().Bind(configuration.GetSection(UrlOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<LimitOptions>().Bind(configuration.GetSection(LimitOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.TryAddSingleton<TimeProvider, MonotonicUtcTimeProvider>();

        var connectionString = configuration.GetConnectionString("ApiArena")!;
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString, sql => sql.CommandTimeout(30)));

        services.AddSingleton<IEmailSender>(sp =>
        {
            var mode = sp.GetRequiredService<IOptions<EmailOptions>>().Value.Mode;
            return string.Equals(mode, "Smtp", StringComparison.OrdinalIgnoreCase)
                ? ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp)
                : ActivatorUtilities.CreateInstance<FileEmailSender>(sp);
        });

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: [ReadyTag]);

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IRunAtomics, RunAtomics>();
        services.AddScoped<IUserDirectory, Identity.UserDirectory>();
        services.AddSingleton<APIArena.Application.Runs.ISimulatorUrls, Web.SimulatorUrls>();
        services.AddApiArenaApplication();
        AddV1Services(services, configuration);
        return services;
    }

    private static void AddV1Services(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebhookOptions>().Bind(configuration.GetSection(WebhookOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<Outbound.ISecretProtector, Outbound.AesGcmSecretProtector>();
        services.AddSingleton<APIArena.Application.Webhooks.ISigningSecretStore, Outbound.SigningSecretStore>();
        services.AddSingleton<APIArena.Application.Webhooks.IWebhookUrlValidator, Outbound.WebhookUrlValidator>();
        services.TryAddSingleton<Outbound.IDnsResolver, Outbound.SystemDnsResolver>();
        services.TryAddSingleton<Outbound.IConnectionFactory, Outbound.SocketConnectionFactory>();
        services.AddSingleton<Outbound.SafeHttpClient>();
        services.AddSingleton<Jobs.OutboxProcessor>();
        services.AddOptions<APIArena.Application.Ops.RetentionOptions>().Bind(configuration.GetSection(APIArena.Application.Ops.RetentionOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddScoped<APIArena.Application.Ops.ISessionCleanup, Jobs.SessionCleanup>();
    }

    /// <summary>Worker-only: job handlers. Webhook handlers exist only when the feature flag is on.</summary>
    public static IServiceCollection AddApiArenaJobHandlers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<Jobs.IJobHandler, Jobs.RetentionHandler>();
        if (configuration.GetValue<bool>(WebhookOptions.Section + ":Enabled"))
        {
            services.AddScoped<Jobs.IJobHandler, Jobs.VerifyTargetHandler>();
            services.AddScoped<Jobs.IJobHandler, Jobs.DeliverWebhookHandler>();
            services.AddScoped<Jobs.IJobHandler, Jobs.FinalizeWebhookRunHandler>();
        }

        return services;
    }
}
