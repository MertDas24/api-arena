# Sınırlı yerel kapasite örneği

2026-10-07: `scripts/load-test.ps1` / `Stage17_BoundedLoadTests`. Hedef kullanıcı tarafından değiştirilemez: ephemeral **loopback Kestrel** ve benzersiz SQL test DB; en fazla 60 s budget. Dış servis hedefi yok. Kaynak JSON: [LOAD_SAMPLE.json](LOAD_SAMPLE.json).

| Ölçüm | Yerel örnek |
|---|---|
| Aktif run | Aynı kullanıcıda 3 (2 retry, 1 timeout) |
| Inbound | 63; 4 immediate + 3 delayed concurrency |
| Gecikme | 3 × 3000 ms; DB transaction bekleme sırasında tutulmaz |
| Polling | 12 istek, concurrency 3; stream yok |
| DB yazımı | 63 observation, 135 event (run lifecycle dahil) |
| Test process CPU | 0.969 CPU s / 3.167 wall s ≈ tek çekirdeğin %30.6'sı |
| Test process peak RAM | 179.1 MiB (API/simulator/client/test harness birlikte) |
| Immediate request p95 | 126.4 ms (60 örnek) |
| Polling p95 | 25.8 ms (12 örnek) |
| Worker contention | 20 synthetic no-op job, 2 processor; duplicate execution 0 |
| Queue lag/drain | Fixture'da başlangıç due lag 1000 ms, gerçek wall drain 282.4 ms |

Queue lag başlangıcı kontrollü clock ile üretilmiş fixture'dır, canlı production telemetry değildir. Peak RAM süreç ömrü boyunca ölçülür; CPU aralığı inbound/polling fazıdır. SQL Server process CPU/RAM, ayrı receiver resource'ları, TLS proxy, container throttling ve uzun süreli soak bu sayılara dahil değildir. Job testi gerçek webhook teslim performansı ölçmez; imza/delivery/crash lease ve dedup ayrı Stage13_14 testlerindedir. Bu kısa örnek kapasite veya SLA garantisi değildir.

## Başlangıç kapasite/maliyet varsayımı
| Kalem | Başlangıç bütçesi / formül | Kanıt/limit |
|---|---|---|
| API + Simulator + Worker + Frontend | Her biri 1 CPU / 512 MiB hard limit (Compose toplam 4 CPU / 2 GiB) | Gerçek Docker RAM/CPU henüz ölçülmedi |
| TLS proxy | 0.5 CPU / 256 MiB limit | ACME/TLS gerçek hosting'de test edilecek |
| DB | Ayrı private SQL Server; DB+OS+disk ihtiyacı ayrıca | Local sample DB resource ölçümü yok |
| Aktif run | 3/kullanıcı; global instance kapasitesi ayrı ölçülür | DB atomic quota testli, çoklu kullanıcı toplam kapasitesi bilinmiyor |
| Delay concurrency | 7 inbound / 3 poll örneğiyle başlanır | Max 5 s/adım, run 200 request / 5 dk; bütçeler satış kotası değil |
| Depolama | Günlük run × ortalama observation/run × ortalama row+index byte × retention günü | Byte/row ve retention worker tamamlanmadan kapasite hesabı doğrulanmaz |
| Hosting maliyeti | Compute + private DB/storage + backup + trafik + domain/mail toplamı | Sağlayıcı/bölge/teklif yok: **para tutarı verilmedi** |

Gerçek ortamda aynı bounded yükü üret, DB dahil CPU/RAM, p50/p95/p99, throttle, queue oldest-due age, error/quota sayısını ölç; resource limitlerini bundan sonra değiştir. Test datası synthetic, test DB'si fixture sonunda silinir. Üç delayed request için gerçek süre beklenir (yük ölçümü); unit/integration correctness testlerine bekleme eklenmedi.
