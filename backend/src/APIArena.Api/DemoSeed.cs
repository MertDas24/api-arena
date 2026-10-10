using APIArena.Application.Projects;
using APIArena.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace APIArena.Api;

/// <summary>
/// Local, explicit opt-in demo data: `dotnet run -- --seed-demo` with APIARENA_DEMO_PASSWORD set.
/// Refuses to run outside Development; ProductionConfigGuard also rejects ApiArena:Demo:SeedOnStartup in Production.
/// Creates a verified demo account and a project whose name is visibly marked as DEMO. No fake reports are created —
/// demo reports come from real runs (scripts/demo.ps1).
/// </summary>
public static class DemoSeed
{
    public const string Email = "demo@apiarena.local";

    public static async Task<int> RunAsync(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            Console.Error.WriteLine("--seed-demo yalnızca Development ortamında çalışır.");
            return 2;
        }

        var password = Environment.GetEnvironmentVariable("APIARENA_DEMO_PASSWORD");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
        {
            Console.Error.WriteLine("APIARENA_DEMO_PASSWORD ortam değişkenini (en az 10 karakter) ayarlayın. Parola koda veya repoya yazılmaz.");
            return 2;
        }

        await app.SeedReferenceDataAsync();
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var user = await users.FindByEmailAsync(Email);
        if (user is null)
        {
            user = new AppUser { UserName = Email, Email = Email, EmailConfirmed = true, DisplayName = "Demo kullanıcı", CreatedAt = clock.GetUtcNow() };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine("Demo hesabı oluşturulamadı: " + string.Join(", ", result.Errors.Select(e => e.Code)));
                return 1;
            }
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            await users.ResetPasswordAsync(user, token, password);
        }

        var projects = scope.ServiceProvider.GetRequiredService<ProjectService>();
        var existing = await projects.ListAsync(user.Id, new APIArena.Application.Common.PageRequest(1, 50, "createdAt", true), "[DEMO]", CancellationToken.None);
        if (existing.Total == 0)
        {
            await projects.CreateAsync(user.Id, new CreateProjectRequest("[DEMO] Ödeme istemcisi", "Yerel demo verisi. Gerçek ödeme veya müşteri verisi içermez."), CancellationToken.None);
        }

        Console.WriteLine($"Demo hesabı hazır: {Email} (parola: APIARENA_DEMO_PASSWORD). Proje: [DEMO] Ödeme istemcisi.");
        return 0;
    }
}
