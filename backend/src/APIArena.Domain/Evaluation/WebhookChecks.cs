using APIArena.Domain.Entities;
using APIArena.Domain.Scenarios;

namespace APIArena.Domain.Evaluation;

public static partial class Evaluator
{
    private static CheckOutcome Acknowledged(IReadOnlyList<DeliveryFact> deliveries)
    {
        const string id = "deliveries-acknowledged", title = "Teslimler HTTP 2xx ile onaylandı";
        const string expected = "Her teslim denemesine 2xx yanıt (yalnızca HTTP onayı; iş doğruluğu kanıtı değildir).";
        var sent = deliveries.Where(d => d.SentAt is not null).ToList();
        if (sent.Count == 0)
        {
            return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Teslim gönderilmedi.", "Hedefe ulaşılamadı veya teslim yapılmadı.", null, [], AbsenceBased: true);
        }

        var bad = sent.Where(d => d.ResponseStatus is null or < 200 or > 299).ToList();
        return bad.Count == 0
            ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{sent.Count} teslimin hepsi 2xx.", "Alıcı her teslimi kabul etti. Bu, işlemin tek kez uygulandığını kanıtlamaz.", null, [])
            : new(id, title, CheckStatus.Failed, EvidenceKind.Observed, expected, $"{bad.Count}/{sent.Count} teslim 2xx değil.",
                "Alıcı bazı teslimleri reddetti veya zaman aşımına uğradı.", "Yinelenen olayları hata yerine 200 (no-op) ile onaylayın; imza/zaman doğrulamasını kontrol edin.", []);
    }

    private static IEnumerable<Func<CheckOutcome>> DuplicateWebhookChecks(EvaluationInput input)
    {
        var deliveries = input.Deliveries ?? [];
        var eventId = deliveries.GroupBy(d => d.EventId).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";

        yield return () => Acknowledged(deliveries);

        yield return () =>
        {
            const string id = "duplicate-was-delivered", title = "Aynı olay en az iki kez teslim edildi";
            const string expected = "Aynı eventId, farklı deliveryId ile ≥ 2 teslim (senaryonun kendisi gerçekleşti mi?).";
            var groups = deliveries.Where(d => d.SentAt is not null).GroupBy(d => d.EventId).Where(g => g.Select(x => x.DeliveryId).Distinct().Count() >= 2).ToList();
            return groups.Count > 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, $"{groups[0].Key} için {groups[0].Count()} teslim.", "Senaryo planlandığı gibi yürütüldü.", null, [])
                : new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Yinelenen teslim gerçekleşmedi.", "Senaryo tam yürütülemediği için alıcının davranışı ölçülemedi.", null, [], AbsenceBased: true);
        };

        yield return () => AdapterProcessedOnce(input, eventId);
    }

    private static CheckOutcome AdapterProcessedOnce(EvaluationInput input, string eventId)
    {
        const string id = "processed-exactly-once", title = "Olay iş mantığında bir kez uygulandı";
        const string expected = "Adapter, olayın etkisinin tam 1 kez uygulandığını bildirir.";
        if (input.Adapter is null)
        {
            return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected,
                input.AdapterError is null ? "Adapter yapılandırılmadı." : $"Adapter yanıtı kullanılamadı ({input.AdapterError}).",
                "200 yanıtı tek kayıt yazıldığını kanıtlamaz; bunun için read-only test adapter gerekir.", "Synthetic run için read-only adapter endpoint'i ekleyin.", []);
        }

        var count = input.Adapter.Events.FirstOrDefault(e => e.EventId == eventId)?.ProcessedCount ?? 0;
        return count == 1
            ? new(id, title, CheckStatus.Passed, EvidenceKind.AdapterReported, expected, "Adapter: processedCount = 1.",
                "Sonuç kullanıcının adapter'ı tarafından bildirildi; platform bunu bağımsız doğrulayamaz.", null, [])
            : new(id, title, CheckStatus.Failed, EvidenceKind.AdapterReported, expected, $"Adapter: processedCount = {count}.",
                count > 1 ? "Aynı olay birden fazla kez uygulandı (ör. çift tahsilat)." : "Olay hiç uygulanmadı.",
                "eventId üzerinde benzersiz kısıt + iş güncellemesini aynı transaction'da yapın; yinelenen olayı no-op olarak 200 ile onaylayın.", []);
    }

    private static IEnumerable<Func<CheckOutcome>> OutOfOrderWebhookChecks(EvaluationInput input)
    {
        var deliveries = input.Deliveries ?? [];
        var def = ScenarioJson.Deserialize<OutOfOrderWebhookDefinition>(input.DefinitionJson);
        var aggregateId = deliveries.FirstOrDefault()?.AggregateId ?? "";
        var staleEventId = deliveries.FirstOrDefault(d => d.EventVersion == def.StaleVersion)?.EventId ?? "";

        yield return () => Acknowledged(deliveries);

        yield return () =>
        {
            const string id = "delivered-out-of-order", title = "Olaylar ters sırada teslim edildi";
            const string expected = "Gerçekleşen gönderim sırası: version 2, sonra version 1.";
            var firstSent = deliveries.Where(d => d.SentAt is not null).GroupBy(d => d.EventVersion).ToDictionary(g => g.Key, g => g.Min(x => x.SentAt!.Value));
            if (!firstSent.TryGetValue(2, out var v2) || !firstSent.TryGetValue(1, out var v1))
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "İki sürüm de gönderilmedi.", "Senaryo tam yürütülemedi.", null, [], AbsenceBased: true);
            }

            return v2 <= v1
                ? new(id, title, CheckStatus.Passed, EvidenceKind.Observed, expected, "v2 önce, v1 sonra gönderildi.", "Planlanan ters sıra gerçekleşti.", null, [])
                : new(id, title, CheckStatus.Inconclusive, EvidenceKind.Observed, expected, "Yeniden denemeler nedeniyle gerçek sıra plandan farklı.",
                    "Ters sıra koşulu oluşmadığından alıcının davranışı bu koşuda ölçülemedi.", null, []);
        };

        yield return () =>
        {
            const string id = "final-state-correct", title = "Son iş durumu en yeni sürümü yansıtıyor";
            var expected = $"Aggregate {aggregateId}: version {def.LatestVersion}, status '{def.LatestStatus}'.";
            if (input.Adapter is null)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Adapter yok.", "Son durum HTTP trafiğinden görülemez.", "Read-only adapter ekleyin.", []);
            }

            var agg = input.Adapter.Aggregates.FirstOrDefault(a => a.AggregateId == aggregateId);
            return agg is not null && agg.Version == def.LatestVersion && agg.Status == def.LatestStatus
                ? new(id, title, CheckStatus.Passed, EvidenceKind.AdapterReported, expected, $"Adapter: v{agg.Version} '{agg.Status}'.", "Adapter tarafından bildirildi.", null, [])
                : new(id, title, CheckStatus.Failed, EvidenceKind.AdapterReported, expected, agg is null ? "Adapter aggregate döndürmedi." : $"Adapter: v{agg.Version} '{agg.Status}'.",
                    "Eski olay yeni durumu ezmiş olabilir.", "Güncellemede 'WHERE Version < @eventVersion' gibi bir sürüm koruması kullanın.", []);
        };

        yield return () =>
        {
            const string id = "stale-event-ignored", title = "Eski (stale) olay uygulanmadı";
            const string expected = "version 1 olayı, version 2 uygulandıktan sonra no-op olarak işlenir (processedCount = 0).";
            if (input.Adapter is null)
            {
                return new(id, title, CheckStatus.Inconclusive, EvidenceKind.None, expected, "Adapter yok.", "Stale olayın nasıl ele alındığı gözlenemez.", null, []);
            }

            var count = input.Adapter.Events.FirstOrDefault(e => e.EventId == staleEventId)?.ProcessedCount ?? 0;
            return count == 0
                ? new(id, title, CheckStatus.Passed, EvidenceKind.AdapterReported, expected, "Adapter: stale olay uygulanmadı.", "Adapter tarafından bildirildi.", null, [])
                : new(id, title, CheckStatus.Failed, EvidenceKind.AdapterReported, expected, $"Adapter: stale olay {count} kez uygulandı.",
                    "Eski olay iş durumunu geri aldı.", "Olay sürümünü aggregate sürümüyle karşılaştırıp eskiyse yok sayın.", []);
        };
    }
}
