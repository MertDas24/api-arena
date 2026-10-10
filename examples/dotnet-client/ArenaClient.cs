using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ApiArena.ExampleClient;

public enum Mode
{
    /// <summary>Common mistakes on purpose: no idempotency key, no/blind retries, no timeout.</summary>
    Naive,

    /// <summary>Corrected behaviour: stable idempotency key, bounded backoff, Retry-After, client timeout.</summary>
    Resilient,
}

public sealed record ClientResult(bool Succeeded, int Requests, string Summary);

/// <summary>
/// Example consumer of the API Arena simulator. The run key is passed in by the caller (read from the RUN_KEY
/// environment variable by Program.cs) and is only ever sent in the Authorization header — never logged or put in a URL.
/// </summary>
public sealed class ArenaClient(HttpClient http, string runKey, Action<string> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private HttpRequestMessage Request(HttpMethod method, string path, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path.TrimStart('/'));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runKey);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    // ------------------------------------------------------------------ retry-503-v1
    public async Task<ClientResult> ChargeAsync(Mode mode, CancellationToken ct)
    {
        var payload = new { amount = 4200, currency = "TRY", description = "API Arena demo (no real payment)" };
        if (mode == Mode.Naive)
        {
            // Mistake: no Idempotency-Key and no retry — a temporary 503 becomes a failed checkout.
            var request = Request(HttpMethod.Post, "payment/charge");
            request.Content = JsonContent.Create(payload);
            using var response = await http.SendAsync(request, ct);
            log($"POST /payment/charge → {(int)response.StatusCode}");
            return new(response.IsSuccessStatusCode, 1, response.IsSuccessStatusCode ? "Ödeme başarılı." : "Ödeme başarısız; yeniden deneme yok.");
        }

        // Correct: one key per logical payment, reused for every retry; bounded exponential backoff with jitter.
        var idempotencyKey = $"order-{Guid.NewGuid():N}";
        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var request = Request(HttpMethod.Post, "payment/charge", idempotencyKey);
            request.Content = JsonContent.Create(payload);
            HttpStatusCode? status = null;
            try
            {
                using var response = await http.SendAsync(request, ct);
                status = response.StatusCode;
                log($"POST /payment/charge (deneme {attempt}, key {idempotencyKey[..14]}…) → {(int)response.StatusCode}");
                if (response.IsSuccessStatusCode)
                {
                    return new(true, attempt, $"Ödeme {attempt}. denemede başarılı.");
                }

                if (!IsTransient(response.StatusCode))
                {
                    return new(false, attempt, $"Kalıcı hata: {(int)response.StatusCode}; yeniden denenmedi.");
                }
            }
            catch (HttpRequestException ex)
            {
                log($"Ağ hatası (deneme {attempt}): {ex.GetType().Name}");
            }

            if (attempt < maxAttempts)
            {
                var wait = Backoff(attempt);
                log($"  {wait.TotalMilliseconds:0} ms bekleniyor…");
                await _delay(wait, ct);
            }
            else
            {
                return new(false, attempt, $"{maxAttempts} denemeden sonra vazgeçildi (son durum {(int?)status}).");
            }
        }

        return new(false, maxAttempts, "Vazgeçildi.");
    }

    // ------------------------------------------------------------------ rate-limit-v1
    public async Task<ClientResult> ListOrdersAsync(Mode mode, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "orders"), ct);
            log($"GET /orders (deneme {attempt}) → {(int)response.StatusCode}");
            if (response.IsSuccessStatusCode)
            {
                return new(true, attempt, "Siparişler alındı.");
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return new(false, attempt, $"Beklenmeyen durum {(int)response.StatusCode}.");
            }

            if (mode == Mode.Naive)
            {
                continue; // Mistake: hammer the server immediately, ignoring Retry-After.
            }

            if (attempt == 3)
            {
                return new(false, attempt, "Hız sınırı sürüyor; daha sonra tekrar denenecek.");
            }

            var wait = RetryAfter(response) ?? TimeSpan.FromSeconds(1);
            log($"  Retry-After: {wait.TotalSeconds:0.#} sn bekleniyor…");
            await _delay(wait, ct);
        }

        return new(false, maxAttempts, "Hız sınırı nedeniyle başarısız.");
    }

    // ------------------------------------------------------------------ timeout-v1
    public async Task<ClientResult> SlowReportAsync(Mode mode, CancellationToken ct)
    {
        if (mode == Mode.Naive)
        {
            // Mistake: no timeout — the user waits as long as the server does.
            using var response = await http.SendAsync(Request(HttpMethod.Get, "reports/slow"), ct);
            log($"GET /reports/slow → {(int)response.StatusCode} (zaman aşımı yok, sonuna kadar beklendi)");
            return new(response.IsSuccessStatusCode, 1, "Yanıt geldi ama kullanıcı uzun süre bekledi.");
        }

        const int maxAttempts = 2;
        var timeout = TimeSpan.FromSeconds(1);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var response = await http.SendAsync(Request(HttpMethod.Get, "reports/slow"), cts.Token);
                log($"GET /reports/slow (deneme {attempt}) → {(int)response.StatusCode}");
                return new(response.IsSuccessStatusCode, attempt, "Rapor alındı.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log($"GET /reports/slow (deneme {attempt}) → {timeout.TotalMilliseconds:0} ms zaman aşımı, bağlantı kapatıldı");
                if (attempt < maxAttempts)
                {
                    await _delay(Backoff(attempt), ct);
                }
            }
        }

        // Shown to the user instead of an endless spinner (the platform cannot observe this message — it is self-reported).
        return new(false, maxAttempts, "Rapor servisi şu an yavaş. Lütfen biraz sonra tekrar deneyin.");
    }

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout;

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var d = date - DateTimeOffset.UtcNow;
            return d > TimeSpan.Zero ? d : TimeSpan.Zero;
        }

        return response.Headers.TryGetValues("Retry-After", out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s)
            : null;
    }
}
