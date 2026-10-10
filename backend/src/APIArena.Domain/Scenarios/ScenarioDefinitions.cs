using System.Text.Json;
using System.Text.Json.Serialization;

namespace APIArena.Domain.Scenarios;

public static class Engines
{
    public const string Retry503 = "retry-503";
    public const string RateLimit = "rate-limit";
    public const string Timeout = "timeout";
    public const string DuplicateWebhook = "duplicate-webhook";
    public const string OutOfOrderWebhook = "out-of-order-webhook";
    public const string Custom = "custom";

    public static bool IsWebhook(string engine) => engine is DuplicateWebhook or OutOfOrderWebhook;
}

/// <summary>Teaching content shown in the catalog and setup wizard (C6: every template carries all of these).</summary>
public sealed record ScenarioContent(
    string Description,
    string LearningGoal,
    IReadOnlyList<string> Setup,
    IReadOnlyList<string> ExpectedTrace,
    IReadOnlyList<string> EvaluationRules,
    IReadOnlyList<string> Unobservable,
    IReadOnlyList<string> Limits,
    string CurlExample,
    string Connection);

public sealed record Retry503Definition(string Method, string Route, int FailAttempts, int FailStatus, int MinBackoffMs, int MaxAttemptsPerOperation);

public sealed record RateLimitDefinition(string Method, string Route, int RetryAfterSeconds, int ToleranceMs, int MaxRateLimitedResponses);

public sealed record TimeoutDefinition(string Method, string Route, int DelayMs, int RecommendedClientTimeoutMs, int MaxDisconnectMs, int MaxRequests);

public sealed record BuiltInScenario(
    string Slug,
    int Version,
    string Engine,
    string EvaluatorVersion,
    string Title,
    string Summary,
    object Definition,
    ScenarioContent Content);

public static class ScenarioJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty definition.");
}

/// <summary>
/// Built-in, versioned catalog. A published (slug, version) is immutable: to change behaviour or content,
/// add a new version entry — never edit an existing one (seeder refuses to overwrite).
/// </summary>
public static class BuiltInScenarios
{
    public const string SimBase = "$SIM_BASE_URL";

    public static readonly BuiltInScenario Retry503 = new(
        "retry-503-v1", 1, Engines.Retry503, "retry-503@1.0",
        "503 sonrası yeniden deneme",
        "Ödeme endpoint'i ilk iki denemede 503 döner, üçüncüde başarılı olur. İstemcin aynı işlemi güvenle yeniden deniyor mu?",
        new Retry503Definition("POST", "payment/charge", FailAttempts: 2, FailStatus: 503, MinBackoffMs: 200, MaxAttemptsPerOperation: 6),
        new ScenarioContent(
            "Sahte bir ödeme servisi geçici olarak kullanılamaz. Aynı işlem (aynı Idempotency-Key) için ilk iki deneme 503 Service Unavailable, üçüncü deneme 200 alır. Gerçek tahsilat yapılmaz.",
            "Geçici hatalarda sınırlı sayıda, bekleyerek ve aynı idempotency anahtarıyla yeniden denemeyi öğrenmek.",
            [
                "İstemcinde ödeme servisi adresini SIM_BASE_URL + /payment/charge yap.",
                "Authorization: Bearer <run-key> başlığını ortam değişkeninden (RUN_KEY) ekle.",
                "Her mantıksal ödeme işlemi için sabit bir Idempotency-Key üret ve yeniden denemelerde aynısını kullan.",
            ],
            ["POST /payment/charge → 503 (deneme 1)", "bekleme", "POST /payment/charge → 503 (deneme 2, aynı anahtar)", "bekleme", "POST /payment/charge → 200 (deneme 3)"],
            [
                "idempotency-key-present: tüm denemelerde Idempotency-Key olmalı.",
                "retried-after-503: 503 alan her işlem yeniden denenmeli.",
                "reached-success: işlem sonunda 200 almalı.",
                "backoff-between-retries: aynı işlemin ardışık denemeleri arasında en az 200 ms olmalı.",
                "no-retry-after-success: 200 sonrası aynı işlem tekrar gönderilmemeli.",
                "bounded-retries: bir işlem için en fazla 6 deneme.",
            ],
            [
                "Uygulamanın veritabanında ödemenin kaç kez kaydedildiği HTTP trafiğinden görülemez.",
                "Kullanıcıya hata mesajı gösterilip gösterilmediği görülemez.",
            ],
            ["Koşu başına 200 istek", "Koşu süresi 5 dakika", "İstek gövdesi en fazla 64 KiB"],
            "curl -i -X POST \"$SIM_BASE_URL/payment/charge\" \\\n  -H \"Authorization: Bearer $RUN_KEY\" \\\n  -H \"Idempotency-Key: order-1001\" \\\n  -H \"Content-Type: application/json\" \\\n  -d '{\"amount\": 4200, \"currency\": \"TRY\"}'\n# curl otomatik yeniden denemez: komutu aynı anahtarla 3 kez çalıştırın.",
            "Koşuyu başlattıktan sonra verilen sahte servis adresini ve erişim anahtarını uygulamanın ortam değişkenlerine koy. Anahtarı koda veya URL'ye yazma."));

    public static readonly BuiltInScenario RateLimit = new(
        "rate-limit-v1", 1, Engines.RateLimit, "rate-limit@1.0",
        "429 ve Retry-After",
        "İlk istek 429 Too Many Requests ve Retry-After: 2 alır. İstemcin belirtilen süreyi bekleyip sonra mı deniyor?",
        new RateLimitDefinition("GET", "orders", RetryAfterSeconds: 2, ToleranceMs: 100, MaxRateLimitedResponses: 2),
        new ScenarioContent(
            "Sahte sipariş servisi koşudaki ilk isteği hız sınırına takar: 429 ve Retry-After: 2. Sunucunun kabul saatine göre 2 saniye dolmadan gelen istek yine 429 alır; süre dolduktan sonra 200 döner.",
            "Retry-After başlığını okuyup saygı göstermeyi ve sunucuyu art arda isteklerle zorlamamayı öğrenmek.",
            ["İstemcinde sipariş listesi adresini SIM_BASE_URL + /orders yap.", "Authorization: Bearer <run-key> başlığını ekle.", "429 aldığında Retry-After değerini oku ve o kadar bekle."],
            ["GET /orders → 429, Retry-After: 2", "≥ 2 sn bekleme", "GET /orders → 200"],
            [
                "respected-retry-after: 429 sonrası ilk istek, sunucu kabul zamanına göre en az 2000 ms − 100 ms tolerans sonra gelmeli.",
                "eventual-success: sonunda 200 alınmalı.",
                "no-hammering: toplam 429 sayısı en fazla 2 olmalı.",
            ],
            ["İstemcinin bekleme süresinde kullanıcıya ne gösterdiği görülemez.", "Ölçüm sunucu kabul zamanına dayanır; ağ gecikmesi bekleme süresini birkaç ms etkileyebilir (100 ms tolerans)."],
            ["Koşu başına 200 istek", "Koşu süresi 5 dakika"],
            "curl -i \"$SIM_BASE_URL/orders\" -H \"Authorization: Bearer $RUN_KEY\"\n# 429 yanıtındaki Retry-After kadar bekleyip komutu tekrar çalıştırın.",
            "Koşuyu başlattıktan sonra sahte servis adresini ve anahtarı uygulamanın ortam değişkenlerine koy."));

    public static readonly BuiltInScenario Timeout = new(
        "timeout-v1", 1, Engines.Timeout, "timeout@1.0",
        "Yavaş yanıt ve zaman aşımı",
        "Rapor endpoint'i 3 saniye gecikir. İstemcin 1 saniyelik zaman aşımıyla bağlantıyı kesip sınırlı şekilde mi davranıyor?",
        new TimeoutDefinition("GET", "reports/slow", DelayMs: 3000, RecommendedClientTimeoutMs: 1000, MaxDisconnectMs: 2000, MaxRequests: 4),
        new ScenarioContent(
            "Sahte rapor servisi her isteği 3 saniye bekletir, sonra 200 döner. İstemci 1 saniyede zaman aşımına uğrayıp bağlantıyı keserse sunucu bunu 'istemci bağlantıyı kesti' olarak kaydeder.",
            "Her dış çağrıya zaman aşımı koymayı ve zaman aşımından sonra sınırsız yeniden denememeyi öğrenmek.",
            ["İstemcinde rapor adresini SIM_BASE_URL + /reports/slow yap.", "Authorization: Bearer <run-key> başlığını ekle.", "HTTP istemcine 1 saniyelik zaman aşımı ver."],
            ["GET /reports/slow → (sunucu 3 sn bekler)", "istemci ~1 sn'de bağlantıyı keser → ClientDisconnected"],
            [
                "client-timeout-observed: en az bir istek gecikme bitmeden kesilmeli (transport kanıtı).",
                "timeout-within-budget: kesilme 2000 ms içinde olmalı.",
                "bounded-retries: toplam en fazla 4 istek.",
                "client-error-handling: her zaman Inconclusive — istemcinin hata mesajı gösterdiği HTTP kaydından çıkarılamaz.",
            ],
            [
                "İstemcinin kullanıcıya hata gösterip göstermediği görülemez.",
                "Arada bir proxy/load balancer bağlantı kesilmesini iletmezse sunucu kopmayı göremez; sonuç Inconclusive olur.",
            ],
            ["Gecikme en fazla 5 sn", "Koşu başına 200 istek", "Koşu süresi 5 dakika"],
            "curl -i --max-time 1 \"$SIM_BASE_URL/reports/slow\" -H \"Authorization: Bearer $RUN_KEY\"",
            "Koşuyu başlattıktan sonra sahte servis adresini ve anahtarı uygulamanın ortam değişkenlerine koy."));

    public static readonly BuiltInScenario DuplicateWebhook = new(
        "duplicate-webhook-v1", 1, Engines.DuplicateWebhook, "duplicate-webhook@1.0",
        "Yinelenen webhook",
        "Aynı ödeme olayı (aynı eventId) iki farklı teslimle, aynı anda gönderilir. Alıcın olayı yalnızca bir kez mi uyguluyor?",
        new DuplicateWebhookDefinition("payment.succeeded", Amount: 4200, Currency: "TRY", Copies: 2, Parallel: true),
        new ScenarioContent(
            "API Arena doğrulanmış webhook hedefine sentetik bir payment.succeeded olayını iki kez (farklı X-Arena-Delivery-Id, aynı X-Arena-Event-Id) paralel gönderir. Gerçek ödeme yoktur.",
            "Webhook alıcısında olay kimliğiyle tekilleştirme (idempotency) ve iş güncellemesini aynı transaction'da yapmayı öğrenmek.",
            [
                "Projende HTTPS webhook hedefi oluştur; bir kez gösterilen imza sırrını alıcına ver.",
                "Hedefi doğrula (alıcı, challenge'a HMAC ile yanıt vermeli).",
                "İsteğe bağlı: read-only adapter yolunu (ör. /webhooks/apiarena/adapter) gir; yoksa iş sonucu Inconclusive olur.",
            ],
            ["POST hedef (event evt_…_1, delivery A)", "POST hedef (event evt_…_1, delivery B) — aynı anda", "Koşu bitince adapter: processedCount?"],
            [
                "deliveries-acknowledged: teslimler 2xx ile onaylanmalı (yalnızca HTTP onayı).",
                "duplicate-was-delivered: aynı event en az iki teslimle gönderilmiş olmalı (senaryo gerçekleşti mi?).",
                "processed-exactly-once: adapter, olayın etkisinin tam 1 kez uygulandığını bildirmeli (AdapterReported).",
            ],
            [
                "200 yanıtı veritabanına tek kayıt yazıldığını kanıtlamaz.",
                "Adapter yanıtı kullanıcının sistemince üretilir; platform bağımsız doğrulayamaz.",
            ],
            ["Koşu başına en fazla 10 teslim", "Koşu başına en fazla 2 istek/sn", "Teslim başına en fazla 3 deneme"],
            "# Webhook senaryosunda curl ile istek göndermezsiniz; API Arena sizin alıcınıza gönderir.\n# Alıcınızın imzayı doğruladığını yerelde örnek alıcıyla deneyin: examples/webhook-receiver",
            "Webhook senaryoları yalnızca doğrulanmış hedeflere gönderilir. Yerelde örnek alıcı (http://localhost:5160) yalnızca geliştirme profilinde izinlidir."));

    public static readonly BuiltInScenario OutOfOrderWebhook = new(
        "out-of-order-webhook-v1", 1, Engines.OutOfOrderWebhook, "out-of-order-webhook@1.0",
        "Ters sıralı webhook",
        "Aynı ödeme için sürüm 2 (captured) önce, sürüm 1 (authorized) sonra gelir. Alıcın eski olayın yeni durumu ezmesini engelliyor mu?",
        new OutOfOrderWebhookDefinition("payment.updated", LatestVersion: 2, LatestStatus: "captured", StaleVersion: 1, StaleStatus: "authorized",
            Amount: 4200, Currency: "TRY", GapMs: 1000),
        new ScenarioContent(
            "API Arena aynı aggregate için önce version 2 (captured), ~1 sn sonra version 1 (authorized) olayını gönderir. Planlanan sıra ile gerçekleşen gönderim sırası ayrı kaydedilir.",
            "Olay sürümünü saklayıp yalnızca daha yeni sürümü uygulamayı (version guard) öğrenmek.",
            ["Doğrulanmış bir webhook hedefi seç.", "İsteğe bağlı adapter yolunu gir; yoksa son durum Inconclusive olur."],
            ["POST hedef (v2 captured)", "~1 sn", "POST hedef (v1 authorized — stale)", "Koşu bitince adapter: son durum?"],
            [
                "deliveries-acknowledged: teslimler 2xx ile onaylanmalı.",
                "delivered-out-of-order: v2'nin v1'den önce gerçekten gönderildiği doğrulanır.",
                "final-state-correct: adapter son durumu v2 'captured' bildirmeli (AdapterReported).",
                "stale-event-ignored: v1 olayı uygulanmamış olmalı (AdapterReported).",
            ],
            ["Son iş durumu HTTP trafiğinden görülemez; adapter olmadan Inconclusive.", "Adapter yanıtı kullanıcının sistemince üretilir."],
            ["Koşu başına en fazla 10 teslim", "Koşu başına en fazla 2 istek/sn"],
            "# Bu senaryoda istekleri API Arena gönderir; örnek alıcı: examples/webhook-receiver",
            "Webhook senaryoları yalnızca doğrulanmış hedeflere gönderilir."));

    public static IReadOnlyList<BuiltInScenario> All { get; } = [Retry503, RateLimit, Timeout, DuplicateWebhook, OutOfOrderWebhook];
}
