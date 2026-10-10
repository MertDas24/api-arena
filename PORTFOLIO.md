# API Arena — portföy anlatımı

**Mevcut seviye:** Çekirdek MVP ve portföy v1 özellikleri (özel senaryo editörü, salt okunur paylaşım, retention, metrikler, yerel webhook demosu) yerelde uygulanmış ve testli; GitHub Actions CI yeşil. Uygulama public olarak yayında değil (hosting/Docker/TLS/egress kapıları açık). Gerçek ödeme/abonelik yok.

## 60 saniyelik demo
Demo öncesinde `scripts/dev.ps1`, kayıt + dev mailbox doğrulama, bir proje ve yanlış/düzeltilmiş retry koşularını hazırla. `scripts/demo.ps1 -Scenario retry-503` gerçek örnek istemciyi çalıştırır; süre hazırlık hariçtir.

| Süre | Göster / anlat |
|---|---|
| 0–10 s | “API Arena, istemcinin kontrollü hata karşısında gerçekten gönderdiği HTTP isteklerini inceler.” Proje→retry-503 senaryosu. |
| 10–20 s | Ayrı simulator URL + kısa ömürlü run-key; secret'i videoya açma. |
| 20–35 s | Yanlış istemci 503'te durur; düzeltilmiş istemci aynı Idempotency-Key ile 503→503→200 ve backoff uygular. |
| 35–50 s | Rapor: expected/observed/evidence sequence; iki koşuyu karşılaştır. 200 DB'nin tek yazıldığını kanıtlamaz. |
| 50–60 s | Timeout için Inconclusive örneği; “gözleyemediğim davranışı geçti saymıyorum.” Yerel webhook demo ile public kapalı profil farkını belirt. |

## CV cümleleri
- “ASP.NET Core/EF Core/SQL Server ve Next.js ile kontrollü API hata senaryoları, canlı trafik gözlemi ve sürümlü kanıta dayalı raporlama sunan API Arena'yı geliştirdim.”
- “Koşu ve sayaç yarışlarını SQL transaction/atomic update ile yönettim; kullanıcı izolasyonu, CSRF, run-key revoke ve SSRF kontrollerini gerçek SQL/Kestrel testleriyle doğruladım.”
- “Yerel webhook demo alıcısında HMAC doğrulama, transaction içinde event dedup ve aggregate version guard uyguladım; at-least-once teslimi ve eksik kanıt sınırlarını belgeledim.”

Uygulamayı "production'da yayımladım" veya "tam güvenli" diye anlatma; yayında değil ve production egress/izleme kapıları açık. Webhook özelliğini "yerel demo, production'da kapalı" diye anlat.

## Mühendislik kararları
- Modüler monolit: Domain saf kararlar, Application use-case, Infrastructure SQL/Identity/network; ayrı API/Simulator/Worker hostları, ortak DB.
- Cookie+CSRF control plane; hash-only run-key ayrı data plane. Production host-only cookie + farklı hostname.
- Senaryo/evaluator snapshot immutable; daha sonraki template değişimi eski raporu değiştirmez.
- DB atomic counter/applock, ağ çağrısı boyunca transaction yok; çok instance'ın paylaşacağı durable outbox lease.
- SSRF: URL policy, tüm A/AAAA kontrolü, connection pinning, TLS original hostname, redirect/response/time limits. Production egress kanıtı yoksa kapalı.
- HTTP observed ve adapter reported ayrı; Inconclusive başarı değil. Genel dayanıklılık puanı yok.
- Non-root/multi-stage/read-only images, explicit migration job, certificate-encrypted Identity key ring; container path henüz gerçek Docker'da doğrulanmadı.

## Yaklaşım farkı
Mockoon, WireMock ve Webhook.site benzer ihtiyaçları karşılayabilir. API Arena'nın yaklaşımı öğrenme hedefi→hazır failure scenario→kötü/iyi istemci→kanıtlı rapor→karşılaştırma akışını tek yerde sade sunmaktır. Bu üstünlük/pazar talebi veya benzersizlik iddiası değildir; rakip özelliklerini ölçmeden karşılaştırma sonucu çıkarılmaz.

## Gerçek ekran görüntüleri
2026-10-07 Playwright ana akışı; synthetic `example.test` hesap ve sentetik ödeme, gerçek çalışan UI/API/SQL. Run-key gösterilmez, Authorization maskelidir.

![Gerçek 503→503→200 canlı trafik](docs/screenshots/live-traffic-1280.png)
![Gözlemlerden üretilmiş rapor](docs/screenshots/passed-report-1280.png)

## Sınırlar ve kanıt
`docs/RELEASE_CHECKLIST.md` tüm kapıları listeler. 94 unit, 145 SQL integration, 29 frontend ve 8 Playwright E2E testi geçti; aynı testler GitHub Actions'ta her push'ta koşar. Ekranlar mobil genişliklerde ve axe ile kontrol edildi. Bounded loopback load + SQL checksum restore drill yapıldı; hosting kapasitesi/maliyeti ölçülmedi. Lisans MIT; güvenlik bildirimi GitHub private vulnerability reporting üzerinden.

Deftere yazılabilir mimari: [docs/MERT_ARCHITECTURE.md](docs/MERT_ARCHITECTURE.md).
