using APIArena.Domain.Entities;
using APIArena.Domain.Scenarios;

namespace APIArena.Domain.Evaluation;

public sealed record ObservationFact(
    long Sequence,
    string? OperationKey,
    int Attempt,
    DateTimeOffset ReceivedAt,
    int PlannedStatus,
    DeliveryOutcome Outcome,
    int? DurationMs,
    int PlannedDelayMs,
    string? RejectReason);

/// <summary>A webhook delivery attempt as recorded by the worker (v1).</summary>
public sealed record DeliveryFact(Guid DeliveryId, string EventId, string AggregateId, int EventVersion, int PlannedOrder, int AttemptNumber,
    DateTimeOffset? SentAt, int? ResponseStatus, string Outcome);

/// <summary>State reported by the user's read-only adapter (v1). Produced by the user's system: never treated as independent proof.</summary>
public sealed record AdapterSnapshot(IReadOnlyList<AdapterAggregate> Aggregates, IReadOnlyList<AdapterEventCount> Events);

public sealed record AdapterAggregate(string AggregateId, string Status, int Version, long Amount);

public sealed record AdapterEventCount(string EventId, int ProcessedCount);

public sealed record EvaluationInput(
    string Engine,
    string DefinitionJson,
    string EvaluatorVersion,
    RunStatus FinalStatus,
    string? IncompleteReason,
    IReadOnlyList<ObservationFact> Observations,
    IReadOnlyList<DeliveryFact>? Deliveries = null,
    AdapterSnapshot? Adapter = null,
    string? AdapterError = null);

public sealed record CheckOutcome(
    string Id,
    string Title,
    CheckStatus Status,
    EvidenceKind EvidenceKind,
    string Expected,
    string Observed,
    string Explanation,
    string? Suggestion,
    IReadOnlyList<long> Evidence,
    bool AbsenceBased = false);

public sealed record EvaluationResult(string EvaluatorVersion, IReadOnlyList<CheckOutcome> Checks, string? IncompleteReason);

/// <summary>
/// Deterministic, versioned evaluation (REP-01/REP-02). Rules:
/// - observations are re-sorted by server sequence, so read order never changes the outcome;
/// - Planned (never confirmed) outcomes are treated as Unknown; Rejected requests are not scenario traffic;
/// - a check without evidence is Inconclusive, never Passed; expected 503/429 responses are not failures by themselves;
/// - if the run is incomplete (cancelled/expired/quota/platform failure), failures that rest only on missing evidence
///   become Inconclusive — and nothing is ever upgraded to Passed;
/// - an exception inside one check makes that check Inconclusive; it cannot mark the report as passed.
/// </summary>
public static partial class Evaluator
{
    public static EvaluationResult Evaluate(EvaluationInput input)
    {
        var facts = input.Observations
            .OrderBy(o => o.Sequence)
            .Select(o => o.Outcome == DeliveryOutcome.Planned ? o with { Outcome = DeliveryOutcome.Unknown } : o)
            .ToList();
        var scenario = facts.Where(o => o.Outcome != DeliveryOutcome.Rejected).ToList();
        var incomplete = input.IncompleteReason
            ?? (input.FinalStatus is RunStatus.Cancelled ? "cancelled" : input.FinalStatus is RunStatus.Expired ? "expired" : input.FinalStatus is RunStatus.Failed ? "platform_failure" : null);

        IEnumerable<Func<CheckOutcome>> checks = input.Engine switch
        {
            Engines.Retry503 => Retry503Checks(ScenarioJson.Deserialize<Retry503Definition>(input.DefinitionJson), scenario),
            Engines.RateLimit => RateLimitChecks(ScenarioJson.Deserialize<RateLimitDefinition>(input.DefinitionJson), scenario),
            Engines.Timeout => TimeoutChecks(ScenarioJson.Deserialize<TimeoutDefinition>(input.DefinitionJson), scenario),
            Engines.Custom => CustomChecks(CustomScenarioCompiler.Parse(input.DefinitionJson), scenario),
            Engines.DuplicateWebhook => DuplicateWebhookChecks(input),
            Engines.OutOfOrderWebhook => OutOfOrderWebhookChecks(input),
            _ => [],
        };

        var results = new List<CheckOutcome>();
        var index = 0;
        foreach (var check in checks)
        {
            index++;
            CheckOutcome outcome;
            try
            {
                outcome = check();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = new CheckOutcome($"check-{index}", "Değerlendirme hatası", CheckStatus.Inconclusive, EvidenceKind.None,
                    "-", "-", $"Bu kontrol değerlendirilirken hata oluştu ({ex.GetType().Name}); sonuç belirsiz sayıldı.", null, []);
            }

            if (incomplete is not null && outcome.Status == CheckStatus.Failed && outcome.AbsenceBased)
            {
                outcome = outcome with
                {
                    Status = CheckStatus.Inconclusive,
                    Explanation = outcome.Explanation + $" Koşu tamamlanmadığı için ({ReasonText(incomplete)}) eksik kanıt başarısızlık sayılmadı.",
                };
            }

            results.Add(outcome);
        }

        return new EvaluationResult(input.EvaluatorVersion, results, incomplete);
    }

    public static string ReasonText(string reason) => reason switch
    {
        "cancelled" => "iptal edildi",
        "expired" => "süresi doldu",
        "quota_exceeded" => "platform istek kotası doldu",
        "platform_failure" => "platform hatası",
        "project_deleted" => "proje silindi",
        _ => reason,
    };

    private static string Seqs(IEnumerable<ObservationFact> o) => string.Join(", ", o.Select(x => "#" + x.Sequence));

    private static IReadOnlyList<long> Ev(IEnumerable<ObservationFact> o) => o.Select(x => x.Sequence).Distinct().ToList();

    private static CheckOutcome NoTraffic(string id, string title, string expected) =>
        new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "İstek gözlenmedi.", "Kanıt olmadan sonuç verilemez.", "Uygulamanın simülatör adresine istek gönderdiğinden emin olun.", [], AbsenceBased: true);

    // ---------------- retry-503 ----------------
    private static IEnumerable<Func<CheckOutcome>> Retry503Checks(Retry503Definition d, List<ObservationFact> obs)
    {
        var groups = obs.GroupBy(o => o.OperationKey ?? "").ToDictionary(g => g.Key, g => g.OrderBy(o => o.Attempt).ToList());

        yield return () =>
        {
            const string id = "idempotency-key-present", title = "Her denemede Idempotency-Key var";
            const string expected = "Tüm denemelerde aynı işlem için sabit Idempotency-Key başlığı.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var missing = obs.Where(o => o.OperationKey is null).ToList();
            return missing.Count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{obs.Count} denemenin hepsinde başlık var.", "Sunucu her istekte Idempotency-Key gördü.", null, Ev(obs))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{missing.Count}/{obs.Count} deneme başlıksız ({Seqs(missing.Take(5))}).",
                    "Başlık olmadan sunucu yeniden denemeyi yeni bir ödeme sanabilir; çift tahsilat riski doğar.",
                    "Her mantıksal ödeme için bir anahtar üretin (ör. sipariş no) ve tüm yeniden denemelerde aynısını gönderin.", Ev(missing));
        };

        yield return () =>
        {
            const string id = "retried-after-503", title = "503 sonrası yeniden denendi";
            const string expected = "503 alan her işlem aynı anahtarla tekrar denenir.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var sent503 = obs.Where(o => o.PlannedStatus == d.FailStatus && o.Outcome == DeliveryOutcome.Sent).ToList();
            if (sent503.Count == 0)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "İstemciye ulaştığı doğrulanmış 503 yok.",
                    "503 yanıtı gönderildiği doğrulanamadığından yeniden deneme beklentisi değerlendirilemedi.", null, Ev(obs.Where(o => o.PlannedStatus == d.FailStatus)));
            }

            var abandoned = sent503.Where(o => !groups[o.OperationKey ?? ""].Any(x => x.Attempt == o.Attempt + 1)).ToList();
            return abandoned.Count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{sent503.Count} adet 503'ün hepsinin ardından yeni deneme geldi.", "Geçici hata sonrası işlem bırakılmadı.", null, Ev(sent503))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{abandoned.Count} adet 503 sonrası deneme gelmedi ({Seqs(abandoned)}).",
                    "İstemci geçici hatada işlemi bıraktı.", "503 gibi geçici hatalarda sınırlı sayıda (ör. 3–5) üstel beklemeli yeniden deneme ekleyin.", Ev(abandoned), AbsenceBased: true);
        };

        yield return () =>
        {
            const string id = "reached-success", title = "İşlem sonunda başarıya ulaştı";
            const string expected = "Aynı işlem için 200 yanıtı istemciye gönderilir.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var ok = obs.Where(o => o.PlannedStatus == 200 && o.Outcome == DeliveryOutcome.Sent).ToList();
            return ok.Count > 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"200 gönderildi ({Seqs(ok.Take(5))}).", "Sunucu başarılı yanıtı iletti.", null, Ev(ok))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, "200 yanıtı gönderilmedi.", $"Başarı için aynı işlemin {d.FailAttempts + 1}. denemesi gerekiyordu.",
                    "Yeniden deneme sayınızın en az 3 olduğundan ve aynı Idempotency-Key'i kullandığınızdan emin olun.", Ev(obs), AbsenceBased: true);
        };

        yield return () =>
        {
            const string id = "backoff-between-retries", title = "Denemeler arasında bekleme var";
            var expected = $"Aynı işlemin ardışık denemeleri arası ≥ {d.MinBackoffMs} ms (sunucu kabul zamanı).";
            var pairs = groups.Values.SelectMany(g => g.Zip(g.Skip(1))).ToList();
            if (pairs.Count == 0)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Yeniden deneme gözlenmedi.", "Bekleme süresi ölçülecek ardışık deneme yok.", null, [], AbsenceBased: true);
            }

            var tooFast = pairs.Where(p => (p.Second.ReceivedAt - p.First.ReceivedAt).TotalMilliseconds < d.MinBackoffMs).ToList();
            var minGap = pairs.Min(p => (p.Second.ReceivedAt - p.First.ReceivedAt).TotalMilliseconds);
            return tooFast.Count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"En kısa aralık {minGap:0} ms.", "İstemci denemeler arasında bekledi.", null, Ev(pairs.SelectMany(p => new[] { p.First, p.Second })))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{tooFast.Count} aralık eşiğin altında (en kısa {minGap:0} ms).",
                    "Beklemesiz yeniden denemeler zaten zorlanan servisi daha da yükler.", "Üstel bekleme kullanın (ör. 250 ms, 500 ms, 1 s) ve jitter ekleyin.",
                    Ev(tooFast.SelectMany(p => new[] { p.First, p.Second })));
        };

        yield return () =>
        {
            const string id = "no-retry-after-success", title = "Başarıdan sonra tekrar gönderilmedi";
            const string expected = "200 alınan işlem tekrar gönderilmez.";
            var succeeded = obs.Where(o => o.PlannedStatus == 200 && o.Outcome == DeliveryOutcome.Sent).ToList();
            if (succeeded.Count == 0)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Başarılı yanıt yok.", "Başarı sonrası davranış gözlenemedi.", null, [], AbsenceBased: true);
            }

            var extra = succeeded.SelectMany(s => groups[s.OperationKey ?? ""].Where(x => x.Attempt > s.Attempt)).Distinct().ToList();
            return extra.Count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, "200 sonrası aynı işlemde yeni deneme yok.", "İstemci başarıyı tanıdı.", null, Ev(succeeded))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"200 sonrası {extra.Count} deneme ({Seqs(extra.Take(5))}).",
                    "Başarılı işlem tekrar gönderildi.", "Başarılı yanıt geldiğinde yeniden deneme döngüsünden çıkın.", Ev(extra));
        };

        yield return () =>
        {
            const string id = "bounded-retries", title = "Yeniden deneme sayısı sınırlı";
            var expected = $"İşlem başına en fazla {d.MaxAttemptsPerOperation} deneme.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var worst = groups.Values.MaxBy(g => g.Count)!;
            return worst.Count <= d.MaxAttemptsPerOperation
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"En çok {worst.Count} deneme.", "Sınırsız döngü gözlenmedi.", null, Ev(worst))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"Bir işlem için {worst.Count} deneme.",
                    "Sınırsız yeniden deneme bir kesintiyi büyütebilir.", "Maksimum deneme sayısı ve toplam süre sınırı koyun.", Ev(worst));
        };
    }

    // ---------------- rate-limit ----------------
    private static IEnumerable<Func<CheckOutcome>> RateLimitChecks(RateLimitDefinition d, List<ObservationFact> obs)
    {
        var threshold = d.RetryAfterSeconds * 1000 - d.ToleranceMs;

        yield return () =>
        {
            const string id = "respected-retry-after", title = "Retry-After süresine uyuldu";
            var expected = $"İlk 429'dan sonraki istek ≥ {threshold} ms sonra (Retry-After {d.RetryAfterSeconds} s − {d.ToleranceMs} ms tolerans).";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var first = obs.FirstOrDefault(o => o.PlannedStatus == 429);
            if (first is null || first.Outcome != DeliveryOutcome.Sent)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "İstemciye ulaştığı doğrulanmış 429 yok.", "Retry-After uyumu ölçülemedi.", null, first is null ? [] : [first.Sequence]);
            }

            var next = obs.FirstOrDefault(o => o.Sequence > first.Sequence);
            if (next is null)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "429 sonrasında istek gelmedi.", "Bekleme süresini ölçecek ikinci istek yok.", null, [first.Sequence], AbsenceBased: true);
            }

            var gap = (next.ReceivedAt - first.ReceivedAt).TotalMilliseconds;
            return gap >= threshold
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"Sonraki istek {gap:0} ms sonra geldi.", "İstemci sunucunun istediği kadar bekledi.", null, [first.Sequence, next.Sequence])
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"Sonraki istek yalnızca {gap:0} ms sonra geldi.",
                    "İstemci Retry-After başlığını dikkate almadı.", "429 yanıtında Retry-After değerini okuyun (saniye veya HTTP tarihi) ve en az o kadar bekleyin.", [first.Sequence, next.Sequence]);
        };

        yield return () =>
        {
            const string id = "eventual-success", title = "Bekleme sonrası başarı";
            const string expected = "İzin penceresinde 200 alınır.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var ok = obs.Where(o => o.PlannedStatus == 200 && o.Outcome == DeliveryOutcome.Sent).ToList();
            return ok.Count > 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"200 gönderildi (#{ok[0].Sequence}).", "İstemci sonunda veriyi aldı.", null, Ev(ok.Take(1)))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, "200 yanıtı gönderilmedi.", "İstemci bekleme sonrası tekrar denemedi veya başarıya ulaşamadı.",
                    "429 sonrası Retry-After kadar bekleyip isteği bir kez daha gönderin.", Ev(obs), AbsenceBased: true);
        };

        yield return () =>
        {
            const string id = "no-hammering", title = "Sunucu art arda isteklerle zorlanmadı";
            var expected = $"Toplam 429 sayısı ≤ {d.MaxRateLimitedResponses}.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var limited = obs.Where(o => o.PlannedStatus == 429).ToList();
            return limited.Count <= d.MaxRateLimitedResponses
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{limited.Count} adet 429.", "İstemci hız sınırında ısrar etmedi.", null, Ev(limited))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{limited.Count} adet 429 ({Seqs(limited.Take(5))}…).",
                    "Hız sınırına takıldıktan sonra beklemeden istek göndermek sınırı uzatır.", "Retry-After'a uyun; gerekirse istemci tarafı hız sınırlayıcı kullanın.", Ev(limited));
        };
    }

    // ---------------- timeout ----------------
    private static IEnumerable<Func<CheckOutcome>> TimeoutChecks(TimeoutDefinition d, List<ObservationFact> obs)
    {
        var disconnects = obs.Where(o => o.Outcome == DeliveryOutcome.ClientDisconnected && (o.DurationMs ?? int.MaxValue) < d.DelayMs).ToList();

        yield return () =>
        {
            const string id = "client-timeout-observed", title = "İstemci zaman aşımıyla bağlantıyı kesti";
            var expected = $"Sunucu {d.DelayMs} ms beklerken istemci bağlantıyı keser (transport kanıtı).";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            if (disconnects.Count > 0)
            {
                return new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{disconnects.Count} istek gecikme bitmeden kesildi ({Seqs(disconnects.Take(5))}).",
                    "Sunucu bağlantının istemci tarafından kapatıldığını gözledi.", null, Ev(disconnects));
            }

            var waited = obs.Where(o => o.Outcome == DeliveryOutcome.Sent).ToList();
            return waited.Count > 0
                ? new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{waited.Count} istek tam {d.DelayMs} ms bekleyip yanıt aldı.",
                    "İstemcinin zaman aşımı yok veya gecikmeden uzun.", $"HTTP istemcinize ~{d.RecommendedClientTimeoutMs} ms zaman aşımı verin (ör. HttpClient.Timeout veya CancellationToken).", Ev(waited))
                : new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Sonuçlar bilinmiyor (Unknown).",
                    "Sunucu bağlantı kesilmesini gözleyemedi; arada bir proxy olabilir.", null, Ev(obs));
        };

        yield return () =>
        {
            const string id = "timeout-within-budget", title = "Zaman aşımı bütçe içinde";
            var expected = $"Bağlantı kesilmesi ≤ {d.MaxDisconnectMs} ms.";
            if (disconnects.Count == 0)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Kesilme gözlenmedi.", "Ölçülecek zaman aşımı yok.", null, [], AbsenceBased: true);
            }

            var late = disconnects.Where(o => o.DurationMs > d.MaxDisconnectMs).ToList();
            var max = disconnects.Max(o => o.DurationMs ?? 0);
            return late.Count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"En uzun kesilme {max} ms.", "Zaman aşımı kullanıcıyı uzun bekletmiyor.", null, Ev(disconnects))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"En uzun kesilme {max} ms.", "Zaman aşımı çok uzun.",
                    $"Zaman aşımını ~{d.RecommendedClientTimeoutMs} ms'ye indirin.", Ev(late));
        };

        yield return () =>
        {
            const string id = "bounded-retries", title = "Zaman aşımı sonrası sınırlı deneme";
            var expected = $"Toplam istek ≤ {d.MaxRequests}.";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            return obs.Count <= d.MaxRequests
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{obs.Count} istek.", "Yavaş servis yeniden denemelerle boğulmadı.", null, Ev(obs))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{obs.Count} istek.", "Zaman aşımından sonra çok fazla yeniden deneme yapıldı.",
                    "Zaman aşımı sonrası deneme sayısını sınırlayın ve devre kesici (circuit breaker) düşünün.", Ev(obs));
        };

        yield return () => new("client-error-handling", "İstemci hatayı kullanıcıya düzgün gösterdi", CheckStatus.Inconclusive, EvidenceKind.None,
            "Zaman aşımında kullanıcıya anlaşılır hata/yeniden deneme seçeneği gösterilir.", "HTTP kaydından gözlenemez.",
            "İstemcinin ekranda ne gösterdiği sunucu tarafındaki trafikten çıkarılamaz. İstemcinin kendi beyanı bağımsız kanıt sayılmaz.", null, []);
    }

    // ---------------- custom ----------------
    private static IEnumerable<Func<CheckOutcome>> CustomChecks(CustomScenarioDefinition d, List<ObservationFact> obs)
    {
        var groups = obs.GroupBy(o => o.OperationKey ?? "").Select(g => g.OrderBy(o => o.Attempt).ToList()).ToList();

        yield return () =>
        {
            const string id = "all-steps-exercised", title = "Tüm adımlar çalıştırıldı";
            var expected = $"Bir işlem için en az {d.Steps.Count} deneme (tüm adımlar).";
            if (obs.Count == 0)
            {
                return NoTraffic(id, title, expected);
            }

            var best = groups.MaxBy(g => g.Max(o => o.Attempt))!;
            var reached = best.Max(o => o.Attempt);
            return reached >= d.Steps.Count
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{reached}. adıma ulaşıldı.", "Senaryonun tüm yanıtları istemciye gönderildi.", null, Ev(best))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"En fazla {reached}. adım.", "İstemci senaryonun tüm adımlarına ulaşmadı.",
                    "Hata yanıtlarında beklenen yeniden deneme davranışını uygulayın.", Ev(best), AbsenceBased: true);
        };

        yield return () =>
        {
            const string id = "retry-after-respected", title = "Retry-After içeren adımlara uyuldu";
            var withRa = d.Steps.Select((s, i) => (s, attempt: i + 1)).Where(x => x.s.RetryAfterSeconds is not null).ToList();
            const string expected = "Retry-After içeren yanıttan sonraki deneme belirtilen süreden önce gelmez (100 ms tolerans).";
            if (withRa.Count == 0)
            {
                return new(id, title, CheckStatus.NotApplicable, EvidenceKind.None, expected, "Senaryoda Retry-After yok.", "Bu kontrol bu senaryoya uygulanmaz.", null, []);
            }

            var pairs = groups.SelectMany(g => g.Zip(g.Skip(1)))
                .Select(p => (p.First, p.Second, ra: d.Steps[Math.Clamp(p.First.Attempt, 1, d.Steps.Count) - 1].RetryAfterSeconds))
                .Where(p => p.ra is not null && p.First.Outcome == DeliveryOutcome.Sent).ToList();
            if (pairs.Count == 0)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Retry-After sonrası deneme yok.", "Ölçülecek aralık yok.", null, [], AbsenceBased: true);
            }

            var early = pairs.Where(p => (p.Second.ReceivedAt - p.First.ReceivedAt).TotalMilliseconds < p.ra!.Value * 1000 - 100).ToList();
            return early.Count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{pairs.Count} aralığın hepsi yeterli.", "Retry-After'a uyuldu.", null, Ev(pairs.SelectMany(p => new[] { p.First, p.Second })))
                : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{early.Count} erken deneme.", "Retry-After dikkate alınmadı.", "Retry-After başlığını okuyun ve bekleyin.",
                    Ev(early.SelectMany(p => new[] { p.First, p.Second })));
        };

        yield return () => new("business-outcome", "Uygulamanın iş sonucu", CheckStatus.Inconclusive, EvidenceKind.None,
            "Uygulama durumu doğru güncellenir.", "HTTP trafiğinden gözlenemez.",
            "Özel senaryolarda platform yalnızca istek/yanıt trafiğini görür; uygulamanın veritabanındaki sonuç doğrulanamaz.", null, []);
    }
}
