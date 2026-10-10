namespace APIArena.Api.Endpoints;

public static class DomainEndpoints
{
    public static IServiceCollection AddDomainEndpoints(this IServiceCollection services) => services;

    public static void MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapProjectEndpoints();
        app.MapScenarioAndRunEndpoints();
        app.MapAccountEndpoints();
        app.MapCustomScenarioEndpoints();
        app.MapShareEndpoints();
        app.MapWebhookEndpoints(((WebApplication)app).Configuration);
    }
}
