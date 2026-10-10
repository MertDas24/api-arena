using System.Text;
using ApiArena.ExampleClient;

// Usage:
//   $env:SIM_BASE_URL = "http://localhost:5151/sim/runs/<runId>"   (copy it from the live run screen)
//   dotnet run --project examples/dotnet-client -- retry-503 naive
// The run key is read from RUN_KEY if set; otherwise it is asked for interactively WITHOUT echoing it,
// so it never lands in source code, command-line arguments or shell history (works in every shell).
// Tolerates common copy-paste leftovers: surrounding quotes or <angle brackets>.
var baseUrl = Environment.GetEnvironmentVariable("SIM_BASE_URL")?.Trim().Trim('"', '\'', '<', '>').Trim();
if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
    || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) || !baseUri.AbsolutePath.Contains("/sim/runs/"))
{
    Console.Error.WriteLine("SIM_BASE_URL geçerli değil.");
    Console.Error.WriteLine("Canlı koşu ekranındaki 'SIM_BASE_URL' değerini (Kopyala düğmesiyle) kullanın, örneğin:");
    Console.Error.WriteLine("  $env:SIM_BASE_URL = \"http://localhost:5151/sim/runs/1b2c3d4e-...\"");
    return 2;
}

var runKey = Environment.GetEnvironmentVariable("RUN_KEY")?.Trim();
if (string.IsNullOrWhiteSpace(runKey))
{
    runKey = ReadSecret("Run key (yapıştırın, ekranda görünmez): ");
}

if (!runKey.StartsWith("aa_rk_", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Anahtar 'aa_rk_' ile başlamalı. Koşu kurulumunda 'Erişim anahtarı oluştur' ile alın.");
    return 2;
}

var scenario = args.ElementAtOrDefault(0) ?? "retry-503";
if (!Enum.TryParse<Mode>(args.ElementAtOrDefault(1) ?? "resilient", ignoreCase: true, out var mode))
{
    Console.Error.WriteLine("Mod 'naive' veya 'resilient' olmalı.");
    return 2;
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
var client = new ArenaClient(http, runKey, Console.WriteLine);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

Console.WriteLine($"Senaryo: {scenario} · mod: {mode} · hedef: {baseUrl}");
ClientResult? result;
try
{
    result = scenario switch
    {
        "retry-503" => await client.ChargeAsync(mode, cts.Token),
        "rate-limit" => await client.ListOrdersAsync(mode, cts.Token),
        "timeout" => await client.SlowReportAsync(mode, cts.Token),
        _ => null,
    };
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Simülatöre bağlanılamadı ({ex.Message}). Simülatör (:5151) çalışıyor mu?");
    return 3;
}

if (result is null)
{
    Console.Error.WriteLine("Senaryo 'retry-503', 'rate-limit' veya 'timeout' olmalı.");
    return 2;
}

Console.WriteLine($"Sonuç: {result.Summary} ({result.Requests} istek)");
Console.WriteLine("Not: 401 aldıysanız anahtar yanlış/iptal edilmiş; 409/410 aldıysanız koşu başlatılmamış veya bitmiş olabilir.");
return result.Succeeded ? 0 : 1;

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine()?.Trim() ?? "";
    }

    var sb = new StringBuilder();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter)
        {
            break;
        }

        if (k.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Length--;
            }

            continue;
        }

        if (!char.IsControl(k.KeyChar))
        {
            sb.Append(k.KeyChar);
        }
    }

    Console.WriteLine();
    return sb.ToString().Trim();
}
