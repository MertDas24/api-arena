# API Arena — Ürün spesifikasyonu

> Bu belge ürün sözleşmesini (kapsam, kanıt ilkesi, güvenlik sınırları) uygulanabilir ayrıntıya indirir.

## 1. Ürün
API Arena, geliştiricilerin uygulamalarını **kontrollü API hata senaryolarına** bağladığı, istek davranışını gözlediği, tekrar çalıştırabildiği ve **kanıta dayalı** rapor aldığı bir web uygulamasıdır. Mockoon, WireMock, Webhook.site gibi benzer araçlar vardır; API Arena'nın hipotezi sade senaryo akışı, açıklayıcı geri bildirim ve uygulamalı öğrenmedir. **Bu bir pazar hipotezidir; doğrulanmış talep veya gelir yoktur.**

Hedef kitle: junior geliştiriciler, API geliştiren öğrenciler, küçük ekipler.

### Ana kullanıcı akışı
kayıt → e-posta doğrulama → proje → senaryo seç → koşu oluştur → run-key al (bir kez gösterilir) → koşuyu başlat → sahte servis URL'sini + anahtarı uygulamaya koy → istek gönder → canlı kanıtlar → koşuyu bitir → rapor → düzelt → yeni koşu → karşılaştır.

### Kanıt ilkesi
- HTTP gözlemi ≠ uygulamanın DB sonucu. 200 yanıtı "tek kayıt yazıldı" kanıtı değildir.
- Kontrol durumları: `Passed`, `Failed`, `Inconclusive`, `NotApplicable`. Inconclusive başarı sayılmaz.
- Kanıt türleri: `Observed` (platformun kendi gözlemi), `AdapterReported` (kullanıcı sisteminin bildirdiği; v1), `SelfReported` (istemci beyanı; bağımsız doğrulama sayılmaz).
- Genel "uygulaman %100 dayanıklı" puanı **yok**. Rapor yalnızca passed/failed/inconclusive/notApplicable sayılarını ve ölçüm kapsamını gösterir.

## 2. Kapsam
| Alan | MVP (00–12) | Portföy v1 (13–17) | Ticari (18–19, isteğe bağlı) |
|---|---|---|---|
| Hesap, oturum, sahiplik | ✔ | ✔ | Ekip/rol |
| retry-503 / rate-limit / timeout senaryoları | ✔ | ✔ | |
| Canlı trafik, rapor, karşılaştırma, JSON/HTML export | ✔ | ✔ | |
| .NET örnek istemci, testler, CI dosyaları | ✔ | ✔ | |
| Webhook hedefleri (SSRF korumalı), duplicate/out-of-order | Kapalı | ✔ (flag; prod egress şartı) | |
| Örnek ödeme alıcısı + read-only adapter | | ✔ | |
| Deklaratif özel senaryo editörü | | ✔ | |
| Salt okunur paylaşım, retention, ops | Temel | ✔ | |
| CI service key, abonelik | | | Ayrı release |

İlk sürüme **girmeyecekler**: gerçek ödeme, rastgele hedeflere yük testi, port taraması, açık proxy, kullanıcı kodu çalıştırma, zorunlu AI puanlama, script/eval motoru, Kubernetes, mikroservis, tünel, ekip yönetimi, abonelik tahsilatı.

## 3. Senaryoların kesin davranışı (MVP)

Ortak: Simülatör adresi `{SIM_BASE_URL}/sim/runs/{runId}/{fixtureRoute}`; `Authorization: Bearer <run-key>` zorunlu. İşlem gruplaması `Idempotency-Key` başlığıyla yapılır (en çok 128 karakter, `[A-Za-z0-9._:-]`). Başlık yoksa istek "anahtarsız" ortak gruba girer ve değerlendirmede `idempotency-key-present` kontrolü **Failed** olur. "Deneme sırası" sunucunun DB'de atomik artırdığı işlem sayaçlarıyla belirlenir (eşzamanlı isteklerde de tek sıra).

Sunucu kabul zamanı (`ReceivedAt`) süreç içi monotonik saatten (başlangıç UTC + Stopwatch) alınır; çoklu instance arası saat farkı NTP toleransına bağlıdır (bkz. DECISIONS ADR-012).

### 3.1 retry-503-v1 — `POST /payment/charge`
- Bir işlem grubunun 1. ve 2. denemesi → `503` + `{"error":"service_unavailable","simulated":true}`; 3. ve sonraki denemeler → `200` + sentetik `{"chargeId":"ch_sim_…","status":"succeeded","simulated":true}`. Gerçek tahsilat yok.
- Kontroller (evaluator `retry-503@1`):
  | id | Passed | Failed | Inconclusive |
  |---|---|---|---|
  | `idempotency-key-present` | tüm denemelerde başlık var | en az bir deneme başlıksız | hiç istek yok |
  | `retried-after-503` | 503 *gönderilen* her grupta sonraki deneme geldi | 503 gönderildi, aynı grupta yeni deneme yok | 503 sonucu bilinmiyor / koşu platform nedeniyle eksik |
  | `reached-success` | bir grupta 200 gönderildi | 200 hiç gönderilmedi | eksik koşu (quota/iptal/expire/platform hatası) |
  | `backoff-between-retries` | aynı gruptaki ardışık denemeler arası ≥ 200 ms | < 200 ms aralık var | tekrar deneme yok |
  | `no-retry-after-success` | 200 sonrası aynı grupta deneme yok | 200 sonrası tekrar deneme var | 200 yok |
  | `bounded-retries` | grup başına ≤ 6 deneme | > 6 deneme | istek yok |

### 3.2 rate-limit-v1 — `GET /orders`
- Koşudaki ilk istek → `429`, `Retry-After: 2`, kilit bitişi = `ReceivedAt + 2 s`. Kilit bitmeden gelen istek → `429` + kalan süre (yukarı yuvarlanmış saniye). Kilit bittikten sonra → `200` + sentetik sipariş listesi.
- 429 senaryonun beklenen parçasıdır; tek başına başarısızlık değildir.
- Tolerans: `ReceivedAt(sonraki) − ReceivedAt(ilk 429) ≥ 2000 ms − 100 ms`.
  | id | Passed | Failed | Inconclusive |
  |---|---|---|---|
  | `respected-retry-after` | ilk 429'dan sonraki ilk istek toleransla ≥ Retry-After | erken istek | 429 sonrası istek yok |
  | `eventual-success` | 200 gönderildi | 200 yok | eksik koşu |
  | `no-hammering` | toplam 429 ≤ 2 | > 2 | istek yok |

### 3.3 timeout-v1 — `GET /reports/slow`
- Sunucu yanıtı 3000 ms geciktirir (iptal edilebilir async), sonra `200`. Örnek istemcinin önerilen timeout'u 1000 ms.
- İstemci bağlantıyı keserse kayıt `ClientDisconnected` + kesilme anındaki süre olur (**transport evidence**). Ara proxy kesilmeyi iletmezse kayıt `Sent`/`Unknown` olabilir; bu durumda dürüstçe Inconclusive.
  | id | Passed | Failed | Inconclusive |
  |---|---|---|---|
  | `client-timeout-observed` | ≥1 istek gecikme bitmeden kesildi | tüm istekler tam 3 s bekleyip `Sent` | yalnızca `Unknown` sonuçlar / istek yok |
  | `timeout-within-budget` | kesilmeler ≤ 2000 ms'de | kesilme > 2000 ms | kesilme yok |
  | `bounded-retries` | toplam istek ≤ 4 | > 4 | istek yok |
  | `client-error-handling` | — | — | **her zaman** Inconclusive: "istemci hata mesajı gösterdi" HTTP kaydından çıkarılamaz |

### 3.4 Platform kaynaklı eksiklik
Quota (koşu başına 200 istek), iptal, süre dolumu (TTL 5 dk) veya platform hatası durumunda raporda `incompleteReason` görünür ve "kanıt yokluğuna dayalı" Failed sonuçlar **Inconclusive**'e çevrilir; hiçbir kontrol bu nedenle Passed olmaz. Quota dolunca simülatör `409` + `X-Arena-Platform: run-quota-exceeded` döner (senaryonun 429'u ile karışmasın diye).

## 4. v1 senaryoları (13–14)
- **duplicate-webhook-v1**: aynı `eventId`, iki farklı `deliveryId` ile (paralel) gönderilir. HTTP ack gözlenir; "tek iş kaydı" yalnızca adapter (AdapterReported) ile değerlendirilebilir, yoksa Inconclusive.
- **out-of-order-webhook-v1**: aynı `aggregateId` için version 2 önce, version 1 sonra. Planlanan sıra ile gerçekleşen gönderim sırası ayrı kaydedilir. Son durum/stale event işleme adapter olmadan Inconclusive.

## 5. Limitler (başlangıç varsayımı; kapasite garantisi değil)
| Limit | Değer | Uygulama yeri |
|---|---|---|
| Kullanıcı başına aktif koşu | 3 | DB applock ile start |
| Kullanıcı başına günlük başlatılan koşu | 30 | aynı |
| Koşu başına inbound istek | 200 | atomik UPDATE |
| Koşu TTL | 5 dk | Run.ExpiresAt |
| Maks gecikme | 5 s | senaryo şeması |
| İstek gövdesi / yanıt / URL / header toplamı | 64 KiB / 64 KiB / 2 KiB / 16 KiB | Kestrel + middleware |
| Koşu başına webhook teslimi | 10 | plan oluşturma |
| Webhook hızı | 2 req/s/koşu | planlama aralığı |

## 6. Ekranlar
landing; kayıt/giriş/şifre sıfırlama/e-posta doğrulama; dashboard ("Şimdi ne yapmalıyım?"); projeler; senaryo kataloğu; koşu kurulum sihirbazı (3 adım); canlı koşu; rapor; karşılaştırma; webhook hedefleri ve özel senaryo (v1); hesap/güvenlik/veri silme; salt okunur paylaşım; geliştirme posta kutusu (yalnızca Development).

## 17 incelemesindeki gerçek durum
V1 kapsamı hedef sözleşmedir; hepsi uygulanmış değildir. Custom compiler/preview/import/version API ve hesap silme testli; custom editör UI, public share, retention worker, metrics/exporter/alerts eksik. 7 gün observations / 30 gün reports otomatik retention tasarım hedefidir. UI eksikliği belirtir. V1 final acceptance Blocked; RELEASE_CHECKLIST.md kanıtları listeler. Production taslak config webhook'u zorla kapatır; Docker/hosting smoke bekliyor.
