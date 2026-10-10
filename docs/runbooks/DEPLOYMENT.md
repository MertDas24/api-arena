# Production hazırlığı ve rollback

Durum: yerel kod/build kanıtları mevcut; Docker/hosting/TLS smoke henüz doğrulanmadı. 15–16 kabul eksikleri yüzünden portföy v1 yayın kapısı **Blocked**. Bu dosya deployment yapılmış olduğu anlamına gelmez.

## Ön koşullar
1. `docs/RELEASE_CHECKLIST.md` kapılarını tamamla; public yayın yetkisi ve hedef hesap/domain seçimi gerekiyor. MIT sahibi tarafından seçildi. GitHub private vulnerability reporting etkinleştirilmeli ve bildirim formu denenmeli.
2. Linux x64 Docker host sağla. SQL Server ayrı yönetilir, private ağda tutulur; DB portunu internete yayımlama. TLS sertifikasının DNS adı bağlantı dizesiyle eşleşmeli. Runtime kullanıcısına DML, migration kullanıcısına geçici DDL izinleri ver; SA kullanma.
3. Internal ağı önceden oluştur: `docker network create --internal --subnet 172.30.80.0/24 api-arena-private`. Ayrı yönetilen SQL ve STARTTLS SMTP relay container'larını bu ağa bağla (ör. `docker network connect api-arena-private sql-service`). SQL/SMTP alias'ları settings ile eşleşmeli. Proxy `.2`, API `.10` adreslerini kullanır; adres çakışması olmadığını doğrula. Uzak managed DB için ayrıca private routing/VPN tasarımı gerekir; bu Compose bunu kurmaz. Webhook bu profilde daima kapalıdır.
4. Repo dışında owner-only dizinde `settings.production.example.json` kopyasını doldur. İki settings dosyası oluştur: runtime DML rolü, migration DDL rolü. Sırları çıktıya/CI artifact'ına yazma. UID 1654 (backend `app`) okuyabilmeli. JSON mount read-only; env önceliklidir. SMTP relay dışarıya gönderimi ayrı bileşende yapar; gerçek mail delivery smoke testi zorunlu.
5. DataProtection için RSA private key içeren PFX oluştur/secret store'dan temin et; TLS sertifikasıyla aynı olmak zorunda değil. `/run/identity/current.pfx` ve parolasını settings'e koy. Repo dışında certificate directory + eski PFX'leri yedekle. Certificate expiry/rotation izlenmeli. DB'de geçmişten kalan plaintext key'ler yeni ayarla otomatik şifrelenmez: fresh production DB tercih et; var olan production key ring için kontrollü yeniden şifreleme veya tüm oturum/token iptali planı gereklidir.
6. OS trust roots + DB/SMTP CA'larını içeren PEM bundle hazırla. TLS validation kapatılmaz. Bundle container sistem trust dosyasına read-only bağlanır.
7. `CONTROL_HOST`, `SIMULATOR_HOST` farklı DNS hostları; DNS kayıtları host IP'sine gider. Caddy ACME 80/443 erişimi ve UID 1000'e yazılabilir `/data`, `/config` dizinleri gerekir. Default image `caddy:2.11.7-alpine` sabittir ([resmi release](https://github.com/caddyserver/caddy/releases/tag/v2.11.7)); production operatörü doğruladığı sürüm+digest'i `CADDY_IMAGE` olarak kaydeder. `latest` kullanma. Docker olmadığı için image pull/digest/taramalar **Blocked**. Proxy health yalnız container port 8090'da, host'a publish edilmez.

Compose değişkenleri (değerler secret store/host shell'de): `RELEASE_TAG`, `CONTROL_HOST`, `SIMULATOR_HOST`, `CADDY_IMAGE`, `APIARENA_PRIVATE_NETWORK`, `APIARENA_SETTINGS_FILE`, `APIARENA_MIGRATION_SETTINGS_FILE`, `APIARENA_CERT_DIR`, `APIARENA_CA_BUNDLE_FILE`, `APIARENA_CADDY_DATA_DIR`, `APIARENA_CADDY_CONFIG_DIR`. Gerçek secrets `.env.example` içine yazılmaz.

## Kontrollü rollout
```bash
docker compose -f infra/compose.production.yml config --quiet
docker compose -f infra/compose.production.yml build api simulator worker frontend
# runtime image'ları tarayıp immutable image digest'lerini release kaydına ekle
# DB backup + restore drill, DDL incelemesi ve bakım penceresi önce
docker compose -f infra/compose.production.yml --profile migration run --rm migrate
docker compose -f infra/compose.production.yml up -d api simulator worker frontend proxy
docker compose -f infra/compose.production.yml ps
```
Migration normal startup'ta çalışmaz; `--migrate` ayrı job'dur. API sadece immutable katalog referans verisini idempotent seed eder. Proxy control `/api`'yi doğrudan API'ye yönlendirir; Next.js standalone rewrite fallback'i build sırasında `http://api:8080` olarak sabittir. Backend forwarded headers yalnız açık proxy `.2`'ye güvenir. Simulator proxy cookie header'ını kaldırır. Backend/frontend/worker non-root, read-only, cap-drop ve CPU/RAM/PID sınırlarıyla çalışır; DB kaynakları ayrıca yönetilir.

Health: API/simulator liveness DB'den bağımsız `/health/live`; readiness container içinden `/health/ready`. Worker heartbeat başarılı DB expiry sweep'inden sonra güncellenir; outbox başarı/lag metriği yerine geçmez. Docker unhealthy kendiliğinden restart yapmaz; alarm alıcısı henüz yok. Frontend health `/login`. Bir readiness 503 durumunda trafik açma; DB down olayında worker heartbeat eskir.

## Public smoke (hedefe yetki verildikten sonra)
- TLS chain/hostname, HTTPS zorlaması, HSTS/CSP; direkt API/DB portlarının internetten kapalı olması.
- Register→gerçek verification mail→login→CSRF mutation→proje→3 çekirdek senaryo→rapor→logout/revoke.
- Browser cookie host-only `__Host-aa_session`, Secure/HttpOnly/Strict; simulator request'te control cookie yok. Simulator cookie tek başına kabul edilmez.
- `/api/features` webhooks=false; UI kapalı özelliği açık bildirir; prod'da dev mailbox/OpenAPI/demo seed erişilemez.
- CPU/RAM + p95 + DB writes/queue lag gerçek hosting'de yeniden ölçülür. Yerel test sonucu hosting kapasitesi değildir.

## Migration ve rollback stratejisi
İlk release migration'ları additive gözden geçirilir. Gelecek değişiklikler expand→backfill (bounded, resumable)→verify→contract ayrı release'leri kullanır. Her migration DDL script'i review edilir; DB boyutu/kilit süresi ölçülmeden public rollout yapma. Migration öncesi write'ları durdur, backup `CHECKSUM` + restore doğrula. Otomatik downgrade/data-destructive Down çalıştırma.

App rollback: önceki image digest'lerine dön, yalnız önceki kod yeni şemayı okuyabiliyorsa. Şema uyumsuzsa maintenance modunda yeni yazımları durdur; backup'ı **yeni DB adına** restore et, doğrula, bağlantıyı operatör onayıyla değiştir. Backup sonrası write'lar RPO kaybıdır; reconciliation/forward-fix kararı kaydedilir. Veri kaybını gizleme. Şema downgrade yerine forward fix tercih edilir. Local restore drill `scripts/restore-drill.ps1`; production restore/şifreli backup/anahtar geri yükleme ayrı kanıttır.

## Olay yönetimi
DB down: readiness 503, runtime credential/TLS/private route kontrolü; kullanıcı DB'sini silme. Queue stuck: oldest pending/due job + expired leases incele; aynı teslim yeniden gönderilebilir (at-least-once), alıcı dedup gerekir. Simulator failure: correlation ID ve platform ProblemDetails incele; beklenen 503/429 ile karıştırma. Secret incident: etkilenen anahtarı iptal/rotate et, oturumları revoke et, backups dahil erişimi gözden geçir. Henüz telemetry exporter/alert alıcısı kurulmadı; endpoint/body/key loglama yasaktır.

## Resmi doğrulama kaynakları (2026-10-07)
- [.NET destek politikası](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): mevcut 10.0.12 LTS korunuyor.
- [Node release politikası](https://nodejs.org/en/about/previous-releases): 24.21.0 LTS korunuyor.
- [Next.js standalone](https://nextjs.org/docs/app/api-reference/config/next-config-js/output): static/public dosyaları ayrıca kopyalanır.
- [DataProtection at-rest](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-encryption-at-rest?view=aspnetcore-10.0): explicit persistence ile encryption ayrıca yapılandırılır.
