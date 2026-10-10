# Runbook: operasyon, metrikler, alarmlar

> **Durum (2026-10-07):** Metrikler kodda üretiliyor ve testli (`Stage16…Metrics_use_only_low_cardinality_tags`). **Bir izleme alıcısı (Prometheus/OTel collector, Azure Monitor vb.) bağlanmadı ve alarmlar kurulmadı.** Aşağıdaki alarm tanımları, alıcı bağlandığında uygulanacak hazır yapılandırmadır.

## Metrikler (`APIArena` meter, düşük kardinalite)
| Ad | Tür | Etiketler | Anlam |
|---|---|---|---|
| `apiarena.simulator.requests` | counter | `result` = scenario / rejected / platform | Simülatör trafiği; platform = 401/409/410/429-ip/503 |
| `apiarena.simulator.plan_ms` | histogram | — | Atomik planlama transaction süresi |
| `apiarena.quota.rejections` | counter | `kind` (kod) | Kota/rate limit reddi |
| `apiarena.outbound.blocked` | counter | `reason` = url_policy / blocked_address | SSRF korumasının reddettiği giden istek |
| `apiarena.webhook.deliveries` | counter | `outcome` | Teslim sonuçları |
| `apiarena.worker.jobs` | counter | `type`, `result` | Outbox işleri |
| `apiarena.runs.active` | gauge | — | Çalışan koşu (worker 30 sn'de örnekler) |
| `apiarena.worker.outbox_lag_seconds` | gauge | — | Vadesi gelmiş en eski bekleyen işin yaşı |

Etiketlerde koşu/kullanıcı kimliği, route, gövde, anahtar, host **yoktur**. Yerelde izlemek için:
```
dotnet tool install --global dotnet-counters
dotnet-counters monitor --counters APIArena -n APIArena.Worker
```
Production'da OpenTelemetry Collector veya platformun .NET metrics entegrasyonu ile `APIArena` meter'ı dışa aktarın (kod değişikliği: `AddOpenTelemetry().WithMetrics(m => m.AddMeter("APIArena"))` + exporter — bağımlılık henüz eklenmedi).

## Önerilen alarmlar
| Alarm | Koşul | İlk müdahale |
|---|---|---|
| DB erişilemez | `/health/ready` 3 ardışık kontrolde 503 (API, Simulator) | Aşağıdaki "DB down" |
| Kuyruk sıkıştı | `outbox_lag_seconds > 300` 10 dk | "Queue stuck" |
| Simülatör hatası | `simulator.requests{result=platform}` oranı > %20 (5 dk) veya liveness düşüşü | "Simulator failure" |
| SSRF artışı | `outbound.blocked` > 50 / 10 dk | Hedefleri ve audit kayıtlarını incele; kötüye kullanım varsa hesabı kısıtla |
| Worker canlılığı | worker heartbeat dosyası (`APIARENA_WORKER_HEARTBEAT`) 2 dk güncellenmedi | Worker restart; log'ları incele |

## Runbook'lar
### DB down
1. `/health/live` 200 ama `/health/ready` 503 ise uygulama ayakta, DB yok — süreçleri yeniden başlatmayın (liveness DB'den bağımsızdır).
2. DB sunucusu/ağı, kimlik bilgisi süresi, disk doluluğu kontrol edilir. 3. DB dönünce readiness kendiliğinden düzelir; çalışan koşular TTL ile Expired olabilir (raporda "süresi doldu" dürüstçe görünür).

### Queue stuck
1. `SELECT TOP 20 Type, Status, Attempts, NextAttemptAt, LeaseOwner, LeaseUntil, LastError FROM OutboxJobs WHERE Status IN (0,1) ORDER BY NextAttemptAt` (yalnızca okuma).
2. Lease'i dolmuş işler bir sonraki worker turunda otomatik yeniden alınır (çökme toparlanması). `no_handler` hatası: webhook bayrağı kapalı worker'da webhook işi — beklenen davranış.
3. Worker ayakta değilse başlatın; sürekli `Failed` olan iş türü için log'ları korelasyon kimliğiyle inceleyin. İşleri elle silmeyin.

### Simulator failure
1. Simülatör `/health/ready`; platform yanıt oranındaki artış hangi koddan (`X-Arena-Platform`) geliyor? `client-rate-limited` → tek IP'den kötüye kullanım; `simulator-overloaded` → eşzamanlılık limiti (500) / ölçek.
2. Kullanıcı tarafı beklenen 503/429 senaryo yanıtları alarm sayılmaz (`result=scenario`).

### Secret incident (anahtar/sır sızıntısı şüphesi)
1. **Run-key / paylaşım bağlantısı:** ilgili koşuda anahtarı yenile (eskisi revoke) veya koşuyu iptal et; paylaşımı iptal et. Değerler yalnızca hash olarak saklandığı için DB dökümü düz anahtar vermez.
2. **Webhook imza sırrı:** hedefte "Sırrı yenile" → yeniden doğrula. DB şifreleme anahtarı (`Webhooks:EncryptionKeys:Current`) sızdıysa: yeni anahtarı `Current` (yeni `CurrentId`) yap, eskisini `Previous` altında bırak, tüm hedeflerin sırlarını yenilet, sonra eski anahtarı kaldır.
3. **DataProtection sertifikası:** yeni sertifikayı `CertificatePath` yap, eskisini `PreviousCertificatePaths` altında tut; tüm oturumları sonlandırmak için `UserSessions` tablosunu temizlemek oturumları düşürür (kullanıcılar yeniden giriş yapar).
4. Repo/CI'da sır bulunduysa: sırrı rotate et, geçmişi temizleme planını hazırla (zorla push yalnızca sahibin onayıyla), olayı kayda geçir.

## Saklama (retention)
Worker her `ApiArena:Retention:IntervalMinutes` (60) dakikada bir lease'li `retention-cleanup` işi kuyruğa alır. Varsayılanlar: gözlemler/olaylar 7 gün, koşu+rapor 30 gün, silinen projeler 7 gün, iptal/süresi dolmuş anahtar ve paylaşımlar 1 gün, iş geçmişi 7 gün, audit 90 gün, süresi dolmuş oturumlar. Her kategori en fazla `BatchSize × MaxBatchesPerCategory` satır siler; tekrar çalıştırmak güvenlidir. **Yedeklerden silinme**, yedek saklama süresi dolana kadar gecikir; gizlilik metninde bu belirtilir.

## Yedek / geri yükleme
Yerel tatbikat: `scripts/restore-drill.ps1` (checksum'lı backup, VERIFYONLY, yeni DB'ye restore, CHECKDB — `docs/TEST_RESULTS.md`). Production'da yönetilen DB'nin PITR/yedek özelliği ve şifreli yedek kullanılmalı; DataProtection sertifikası ve webhook şifreleme anahtarları **ayrı** ve güvenli yedeklenmelidir (DB yedeği tek başına şifreli sırları açamaz).
