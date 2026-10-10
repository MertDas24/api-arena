# Portföy v1 yayın kapısı — 2026-10-07

**Karar: Blocked (dış erişim).** Çekirdek MVP ve portföy v1 kod/test kapıları (15–16 dahil) yerelde geçti; kalan kapılar hosting/Docker/TLS/remote CI/GitHub reporting/egress/izleme alıcısı erişimine bağlı. Production dosyaları hazırlandı, container/TLS/hosting yolu çalıştırılmadı. Public deployment, repository push ve ticari aşamalar yapılmadı.

`passed`: belirtilen kapsamda gerçek kanıt; `blocked`: eksik kod/altyapı/erişim; `not-applicable`: seçilmeyen kapsam, gerekçesiyle. Production doğrulaması yerel testlerden çıkarılmaz. Ayrıntılı requirement→test eşlemesi `ACCEPTANCE.md`, exact komutlar `TEST_RESULTS.md`.

## Zorunlu kapılar
| Kapı / requirement | Durum | Kanıt / gerekçe |
|---|---|---|
| Backend production build | passed | Release build + 94 unit / 139 gerçek SQL integration; mevcut analyzer uyarıları var |
| Frontend production build | passed | Next standalone, static/public paketleme, lint/typecheck, 29 Vitest |
| Hesap/oturum/CSRF, AUTH-01…11 | passed | Stage02 + Stage11 production cookie/HSTS; logout/reset DB session revoke |
| Hesap silme, AUTH-12 | passed | Stage16_AccountDeletionTests; public backup gecikmesi ayrı risk |
| Sahiplik/IDOR/DTO/paging/proje silme, OWN-01…05 | passed | Stage03/04/06/07_08/13_14; yabancı nested kaynak 404 |
| Snapshot/state/quota/anahtar/feed/maskeleme, RUN-01…08 | passed | Stage04/05/06/07_08 concurrency, expiry, rotate/revoke, immutable rapor |
| Üç çekirdek akış, SIM-01…10 | passed | Stage05/Stage09: 503, Retry-After, timeout; Kestrel transport; kötü/iyi örnek istemci |
| Rapor/export/karşılaştırma, REP-01…06 | passed | EvaluatorTests, Stage07_08, frontend bileşenleri + E2E export; Inconclusive başarı değil |
| Özel senaryo backend sandbox, SIM-11/SEC-06 | passed | Stage15: strict schema/import, XSS/plaintext, traversal/limit, preview==run, owner isolation, eski snapshot |
| Özel senaryo editörü UI, aşama 15 | passed | Editör (adımlar, sunucu önizlemesi, geçersiz tanım hatası, içe/dışa aktarma, sürüm, arşiv); E2E v1-features + 360 px + axe 0 |
| Yerel webhook SSRF/imza/verification/outbox, WH-01…08 | passed | OutboundPolicyTests, Stage13_Safe + Stage13_14; fake DNS ve kontrollü yerel alıcı, dış site saldırısı yok |
| Duplicate/out-of-order/adapter, WH-09…11 | passed | Stage13_14 gerçek örnek alıcı, SQL unique transaction/version guard, retry/lease recovery; adapter sonuçları AdapterReported |
| Worker gönderim-sonrası crash fixture | passed (durum fixture'ı) | Stage13_14…Crash_after_send_before_record…: alıcı teslimi aldı, kayıt yazılmadan lease'i dolmuş iş → aynı delivery id ile yeniden gönderim, ödeme bir kez uygulandı. Gerçek process kill değil, DB durumunun birebir kurgusu |
| Production egress / public webhook, WH-12 | blocked | Hosting network policy uygulanmadı. Production Compose env webhooks=false zorlar; local exception=false. Attestation tek başına kanıt değil |
| UI/webhook kapalı durumu | passed | /api/features + webhook ekranı E2E (360 px, axe 0); Stage13_14 flag-off testleri. Webhook açık UI akışı yalnızca backend testleriyle kanıtlı |
| UI/mobile/klavye/a11y, UX-01…07 | passed | Mevcut 9 ekran × 360/390/768/1440; axe serious/critical 0 (390/1440); klavye; gerçek ana akış. V1 yeni/eksik ekranlarını kapsamaz |
| Public share/expiry/revoke/privacy, REP-07 | passed | ShareService + ReportSanitizer; Stage16 sanitize/noindex/hash/revoke/expiry/proje+hesap silme; E2E anonim görüntüleme ve iptal |
| Retention leased/idempotent/bounded, OPS-04 | passed | Lease'li outbox işi + batch sınırı; Stage16 retention testi (ikinci tur 0 satır) |
| Metrik/exporter/alerts, OPS-05 | blocked (alıcı) | APIArena meter + gauge'lar testli (düşük kardinalite); alarm tanımları OPERATIONS.md. Exporter/izleme alıcısı ve bildirim kanalı yok — alarm kurulmuş sayılmaz |
| Yerel migrations + job, OPS-03 | passed | 3 migration fresh DB; `--migrate` job iki kez, seed idempotent. Runtime normal startup migration yapmaz |
| Yerel backup/restore, OPS-06 | passed | checksum backup→VERIFYONLY→yeni DB restore→CHECKDB→3 migration + synthetic marker. Production encryption/key restore ayrıca blocked |
| DataProtection yeni production key ring | passed | ProtectKeysWithCertificate; missing cert guard; Stage11 DB encryptedSecret. Eski plaintext key'ler otomatik çevrilmez; production cert erişimi/rotation restore henüz yok |
| Proxy trust/SQL connection alias bypass, SEC-01/OPS-02 | passed | Stage17_ProxyTests + ProductionConfigGuardTests; boş liste loopback-only, explicit IP listesi, Trust Server Certificate=yes reddi |
| ProblemDetails/XSS/export filename, SEC-02/04/07 | passed | Stage11, Stage07_08, components.test; prod stack yok, plaintext içerik |
| Secret taraması, SEC-03 | passed | gitleaks 8.30.1 checksum doğrulandı, çalışma ağacında 0 bulgu. Commit/geçmiş yok; yayın öncesi tekrar taranır |
| Dependency taraması, SEC-05 | passed | .NET 10 projede vulnerability yok; npm production 0; dev-only braces 5 high ayrı triage (mevcut F3); container scan yok |
| Container/config/TLS/private DB, OPS-08 | blocked | Non-root/minimal multi-stage/resource/health/read-only config var; YAML syntax/static checks geçti. Docker yok: build/pull/runtime/CA/TLS/health/container scan doğrulanmadı |
| Sınırlı yerel load, OPS-09 | passed | 3 run, 63 inbound, 12 polling, 20 jobs/2 processors; CPU/RAM/p95/write/fixture lag ölçümü `LOAD_SAMPLE.json`; SQL CPU/RAM ve gerçek hosting kapasitesi ölçülmedi |
| Streaming load | not-applicable | Ürün polling kullanır; SSE/WebSocket uygulanmadı |
| Remote CI, OPS-07 | blocked | Workflow mevcut; remote/commit/push yok, GitHub koşusu doğrulanmadı |
| README/screenshot/demo/docs, DOC-01…05 | passed | English ana + Türkçe, gerçek synthetic E2E screenshots, PORTFOLIO/MERT_ARCHITECTURE/deploy/rollback/load docs. Docker quickstart kapsamı doğrulanmadı |
| License | passed | Mert 2026-10-07'de MIT seçti; LICENSE eklendi, ADR-031 |
| Responsible reporting | blocked | GitHub private vulnerability reporting seçildi; hosted repository/kanal etkinleştirmesi/bildirim testi yok |
| Public deployment/smoke | blocked | Hosting/domain/SMTP/cert/secret/account erişimi ve dış yayın yetkisi yok |
| Abonelik/gerçek payment dedup | not-applicable | Ticari 18–19 seçilmedi; test edilen payment event'ler synthetic örnek alıcıya aittir |

## Hazır
Release kod/config/runbook taslağı, MIT, secret dış mount, TLS proxy ve host ayrımı, explicit migration job, rollback/data migration stratejisi; yerel doğrulamalar ve gerçek screenshots.

## Eksik (kod/kanıt)
1. Docker fresh setup, image build/scan/digest kaydı, production cert/CA/secret mount'ları, gerçek TLS ve cookie izolasyonu smoke (Docker/hosting yok).
2. Metrik exporter + izleme alıcısı + bildirim kanalı (kod tarafı hazır, alıcı yok).
3. Webhook açık durumda UI'nin tarayıcı E2E'si (backend akışı uçtan uca testli; E2E sunucuları flag kapalı çalışır).
4. Remote CI koşusu ve git geçmişi secret taraması (commit/remote yok).

## Kullanıcıdan/ortamdan gerekiyor
- Hosting hedefi + iki ayrı DNS hostu + public yayın yetkisi (bu tur push/yayın yapmadı).
- SQL private erişimi ve doğrulanan TLS chain, runtime/migration rolleri; SMTP sağlayıcısı/relay + verified sender.
- PFX private key/parolası, DB+PFX encrypted backups ve key restore tatbikatı; secret store'a değerler girilir, sohbete yazılmaz.
- GitHub repository + private vulnerability reporting açılması, CI çalıştırma/yayın yetkisi; alert bildirim kanalı.
- Production webhook seçilirse ayrıca worker egress enforce + kontrollü bypass test kanıtı. Mevcut kapalı profil bunu açmaz.

Lisans ve güvenlik kanalı için ek karar istenmiyor: MIT seçildi, GitHub özel reporting kanalı seçildi; kanalın gerçek erişimi hâlâ kurulmalı. Kritik kod/kanıt kapıları geçmeden public v1 release yapılmaz.