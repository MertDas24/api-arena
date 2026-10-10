using APIArena.Application.Access;
using APIArena.Application.Projects;
using APIArena.Application.Runs;
using APIArena.Application.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace APIArena.Application;

public static class ApplicationServices
{
    public static IServiceCollection AddApiArenaApplication(this IServiceCollection services)
    {
        services.AddScoped<AccessScope>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ScenarioCatalog>();
        services.AddScoped<ScenarioSeeder>();
        services.AddScoped<CustomScenarioService>();
        services.AddScoped<RunEventLog>();
        services.AddScoped<RunService>();
        services.AddScoped<Simulation.SimulationService>();
        services.AddScoped<ObservationService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<Webhooks.AuditLog>();
        services.AddScoped<Webhooks.WebhookTargetService>();
        services.AddScoped<Webhooks.DeliveryQueryService>();
        services.AddScoped<IRunLifecycleParticipant, Webhooks.WebhookRunPlanner>();
        services.AddScoped<Reports.IWebhookFinalizationScheduler, Webhooks.WebhookFinalizationScheduler>();
        services.AddScoped<Projects.IProjectDeletionParticipant, Webhooks.WebhookDeletionParticipant>();
        services.AddScoped<Reports.RunFinalizer>();
        services.AddScoped<IRunFinalizer>(sp => sp.GetRequiredService<Reports.RunFinalizer>());
        services.AddScoped<Reports.ReportService>();
        services.AddScoped<Reports.ShareService>();
        services.AddScoped<Ops.RetentionService>();
        services.AddScoped<Projects.IProjectDeletionParticipant, Reports.ShareDeletionParticipant>();
        return services;
    }
}
