# Mimari ve uygulama kararları (ADR)

Biçim: Bağlam → Karar → Neden alternatif seçilmedi → Durum. Değişen karar silinmez; "Superseded by ADR-x" eklenir.

## ADR-001 — .NET 10 LTS
- **Bağlam:** C3 desteklenen LTS ister. 2026-10-07'de dotnet.microsoft.com destek sayfası: .NET 10 LTS (10.0.12, destek 14 Kasım 2028), .NET 8 LTS 10 Kasım 2026'da bitiyor, .NET 11 RC.
- **Karar:** .NET 10, `global.json` SDK `10.0.401`, `rollForward: latestFeature`.
- **Alternatif:** .NET 8 bir ay içinde destek dışı; .NET 11 henüz GA değil.
- **Durum:** Kabul.

## ADR-002 — Next.js 16 + React 19 + Tailwind 4 + npm
- **Bağlam:** nextjs.org/support-policy: 16.x Active LTS, 15.x Maintenance. npm registry: next 16.4.0, react 19.3.0, tailwindcss 4.3.3.
- **Karar:** Next.js 16 App Router, TypeScript strict, Tailwind 4, npm (Node 24 kurulu, lockfile commit edilir).
- **Alternatif:** pnpm kurulu değil; ek araç gereksinimi devri zorlaştırır.
- **Durum:** Kabul.

## ADR-003 — Veritabanı: yerel SQL Server (native), Docker opsiyonel
- **Bağlam:** Geliştirme makinesi Windows 11 x64; SQL Server 2025 Developer (`MSSQLSERVER`) çalışıyor, Windows auth ile sysadmin. Docker kurulu değil. Apple Silicon değil.
- **Karar:** Tek sağlayıcı **SQL Server** (EF Core SqlServer). Varsayılan yerel yol: native instance + Windows auth. Docker Compose dosyası (SQL Server 2022 Linux image) macOS/Linux ve CI için sağlanır; bu makinede **çalıştırılamadığı için doğrulanmış sayılmaz**. Apple Silicon için: Docker Desktop + Rosetta ile `mssql/server` resmi olarak desteklenmez; uzak geliştirme SQL Server'ı (ör. Azure SQL Developer tier) önerilir — bu yol da doğrulanmadı.
- **Alternatif:** SQLite (C3 yasaklıyor), PostgreSQL (gerek yok, x64 Windows'ta SQL Server mevcut).
- **Durum:** Kabul.

## ADR-004 — Katmanlar ve EF erişimi
- **Karar:** Domain (saf), Application (`IAppDbContext` arayüzü + servisler), Infrastructure (`AppDbContext : IdentityDbContext`), Api/Simulator/Worker host'ları. MediatR/CQRS/generic repository yok.
- **Neden:** C3 yalın bağımlılık ister; servisler gerçek SQL Server'a karşı entegrasyon testiyle doğrulanır.
- **Durum:** Kabul.

## ADR-005 — Oturum: Identity cookie + same-origin proxy + antiforgery
- **Karar:** Identity application cookie (HttpOnly, SameSite=Strict, prod'da `Secure` ve `__Host-aa_session`; Development/Testing'de `SameAsRequest`, ad `aa_session` — test HttpClient'ı http üzerinde Secure cookie göndermez). CSRF: `GET /api/auth/csrf` antiforgery token'ı JSON olarak döner, unsafe isteklerde `X-CSRF-TOKEN` zorunlu + `Origin` allowlist. Token'lar tarayıcı storage'ına yazılmaz (bellekte).
- **Alternatif:** JWT + localStorage (C3 yasaklıyor).
- **Durum:** Kabul.

## ADR-006 — Geliştirme mail sink: dosya tabanlı
- **Bağlam:** Docker yok → Mailpit/smtp4dev container'ı çalıştırılamıyor.
- **Karar:** `Email:Mode=File` (yalnızca Development/Testing): e-postalar `.local/mail/*.json` dizinine yazılır, yalnızca Development'ta açık `/api/dev/mailbox` endpoint'i ve `/dev/mailbox` sayfası listeler. Production'da `Email:Mode=Smtp` zorunlu (MailKit); başlangıç doğrulaması `File` modunu Production'da reddeder. Gönderim hatası kullanıcıya "gönderilemedi" olarak yansır.
- **Durum:** Kabul.

## ADR-007 — Run-key biçimi ve saklama
- **Karar:** `aa_rk_` + 32 bayt CSPRNG base64url (256 bit). DB'de yalnızca SHA-256 hash + ilk 10 karakterlik görüntüleme öneki. Yavaş hash gerekmez (yüksek entropi). Yeni anahtar oluşturma önceki aktif anahtarları revoke eder (rotation). Anahtar ömrü koşu TTL'i ile sınırlı.
- **Durum:** Kabul.

## ADR-008 — İşlem gruplaması `Idempotency-Key` başlığı
- **Karar:** Retry grubu `Idempotency-Key` başlığıyla belirlenir. Başlıksız istekler tek "anahtarsız" gruba düşer (simülasyon demo için çalışır) fakat `idempotency-key-present` kontrolü Failed olur.
- **Alternatif:** Başlıksız isteği 400 ile reddetmek — curl ile tek istek öğrenimini zorlaştırır.
- **Durum:** Kabul.

## ADR-009 — Canlı akış: append-only olay günlüğü + polling
- **Karar:** `RunEvents` (RequestReceived, ResponseOutcome, yaşam döngüsü) koşu sayacından sequence alır; `afterSequence` polling 1.5 s. SSE MVP'de yok.
- **Neden:** Proxy buffering/heartbeat riskleri olmadan reconnect güvenli; duplicate olmaz.
- **Durum:** Kabul.

## ADR-010 — Atomiklik: koşullu UPDATE/MERGE + sp_getapplock
- **Karar:** Sequence/quota tek `UPDATE … OUTPUT`; deneme sayacı `MERGE … WITH (HOLDLOCK)`; aktif koşu kotası `sp_getapplock` (kullanıcı kapsamlı, transaction ömürlü). Ağ beklemesi transaction dışında. Ham SQL yalnızca Infrastructure'da ve parametreli.
- **Durum:** Kabul.

## ADR-011 — Proje silme: soft delete + retention
- **Karar:** `Projects.DeletedAt`; silmede aktif koşular iptal, run-key'ler ve paylaşımlar revoke edilir; veriler retention işinde (7 gün) kalıcı silinir. Silinmiş proje hiçbir endpoint'te görünmez (404).
- **Durum:** Kabul.

## ADR-012 — Zaman kaynağı
- **Karar:** `TimeProvider` DI. Production'da `MonotonicUtcTimeProvider` (başlangıç UTC + `Stopwatch`), NTP sıçramalarından etkilenmez. Testlerde `FakeTimeProvider`. Çoklu instance arası fark NTP'ye bağlı (kalan risk).
- **Durum:** Kabul.

## ADR-013 — Test çatısı
- **Karar:** xUnit v3 3.2.x (4.0 yeni major; API değişikliklerini devir sırasında riske etmemek için), `Microsoft.AspNetCore.Mvc.Testing`, gerçek SQL Server'da test başına benzersiz veritabanı (`ApiArena_Test_<guid>`), sonunda drop. Frontend: Vitest + Testing Library; E2E: Playwright (Chromium).
- **Durum:** Kabul.

## ADR-014 — Platform quota yanıtı 409
- **Karar:** Koşu inbound quota'sı dolunca simülatör `409` + `X-Arena-Platform: run-quota-exceeded` döner; senaryonun 429'u ile karışmaz. Rapor "platform limiti nedeniyle eksik" der.
- **Durum:** Kabul.

## ADR-015 — Frontend CSP: nonce + dinamik render; `cacheComponents` kapalı
- **Bağlam:** `create-next-app@16.4.0` iskeleti `cacheComponents`/`partialPrefetching` ile geldi. Nonce tabanlı CSP her istekte dinamik render ister.
- **Karar:** `src/proxy.ts` her HTML isteğine nonce'lu CSP üretir; root layout `headers()` okuyarak dinamik render'a geçer; `cacheComponents` kapatıldı. Google Fonts (`next/font/google`) kaldırıldı, sistem font yığını kullanılıyor (build sırasında dış ağ bağımlılığı ve üçüncü taraf isteği yok).
- **Alternatif:** `script-src 'unsafe-inline'` — XSS savunmasını zayıflatır.
- **Durum:** Kabul.

## ADR-016 — Sunucu tarafı oturum deposu
- **Bağlam:** Kabul kriteri "oturum çıkıştan sonra geçersiz". Yalnızca cookie tabanlı ticket'ta çalınmış cookie süre dolana kadar geçerlidir; security stamp doğrulaması aralıklıdır.
- **Karar:** Identity cookie `SessionStore = DbTicketStore`; cookie yalnızca korumalı oturum anahtarı taşır, ticket `UserSessions` tablosunda DataProtection ile şifreli. Logout satırı siler; parola sıfırlama ve logout-all kullanıcının tüm satırlarını siler. Security stamp doğrulama aralığı 1 dk (ikincil katman).
- **Durum:** Kabul.

## ADR-017 — Zaman duyarlı token sağlayıcı
- **Bağlam:** Identity `DataProtectorTokenProvider` sistem saatini kullanır; süresi dolmuş token testi gerçek bekleme gerektirirdi.
- **Karar:** `TimeAwareTokenProvider` aynı yapıyı (DataProtection şifrelemesi, security stamp bağlama) `TimeProvider` ile kurar. Reset 30 dk, e-posta doğrulama 24 saat. Kriptografi ASP.NET Core DataProtection'dadır; özel algoritma yok.
- **Durum:** Kabul.

## ADR-018 — Varsayılan olarak kimlik doğrulama zorunlu
- **Karar:** Control API'de `FallbackPolicy = RequireAuthenticatedUser`; anonim endpoint'ler (`/api/auth/*` bazıları, health, dev mailbox, paylaşım) açıkça `AllowAnonymous`. Anonim istemci bilinmeyen route'ta 401 alır (route keşfi yok).
- **Durum:** Kabul.

## ADR-019 — Kayıtta otomatik giriş yok; doğrulanmamış hesaba giriş izinli
- **Karar:** Kayıt her durumda aynı 202 yanıtını döner (mevcut adres için "zaten hesabınız var" bildirimi e-postası). Doğrulanmamış hesap giriş yapabilir ama koşu başlatamaz (`ApiArena:Auth:RequireVerifiedEmailForRuns`, varsayılan true, her ortamda). E-posta gönderilemezse 503 `email_not_sent` döner; "gönderildi" denmez.
- **Durum:** Kabul.

## ADR-020 — Simülatör route'u: run başına tek fixture
- **Karar:** `/sim/runs/{runId}/{**route}` yalnızca kimlik doğrulamadan sonra koşunun snapshot'ındaki tek (metot, route) çiftiyle eşleşir; diğer her şey `404 unknown_fixture_route` ve `Rejected` gözlem olarak kaydedilir (kota tüketir). Control API ile aynı host'ta değil, catch-all çakışması yok.
- **Durum:** Kabul.

## ADR-021 — Gözlem sonucu `CompletedAt` üzerinden yazılır
- **Bağlam:** Koşu, gecikmedeki bir istek sürerken bitirilirse finalizer `Planned` gözlemi `Unknown` yapar.
- **Karar:** Gerçek tel sonucu `CompletedAt IS NULL` koşuluyla yine yazılır (daha doğru bilgi). Önceden üretilmiş rapor, üretildiği andaki bilgiyle değişmeden kalır (immutable).
- **Durum:** Kabul.

## ADR-022 — E2E çalıştırma: önce derle + migrate, sunucular `--no-build`
- **Karar:** `scripts/e2e.(ps1|sh)` backend ve frontend'i derler, `ApiArena_E2E` veritabanına migration uygular, sonra Playwright `webServer` ile `dotnet run --no-build` ve `next start` başlatır. Paralel `dotnet run` derleme çakışması ve migration sırası riski böylece yok.
- **Durum:** Kabul.

## ADR-023 — Test saati izolasyonu
- **Bağlam:** Entegrasyon testleri tek veritabanını paylaşır; global süre dolumu taraması bir testin ileri sardığı sahte saatle diğer testlerin koşularını sonlandırıyordu.
- **Karar:** Global tarama yapan test daha erken bir sahte tarihle başlar (`ApiFactory(db, start)`). Ürün davranışı değişmedi. Test factory'leri `ValidateOnBuild`/`ValidateScopes` ile çalışır.
- **Durum:** Kabul.

## ADR-024 — Vurgu rengi kontrastı
- **Karar:** `accent-500` #7c6cf6 → #6a5ae0 (beyaz metinle ≈5.1:1), hover daha koyu (#5a4ad4). axe wcag2aa ölçümüne dayanır.
- **Durum:** Kabul.

## ADR-025 — Giden istekleri yalnızca Worker yapar
- **Karar:** Hedef doğrulama, teslim ve adapter çağrıları outbox işi olarak Worker'da çalışır; Control API ve Simulator dış ağa çıkmaz. Böylece egress kısıtı tek bir bileşene uygulanır.
- **Durum:** Kabul.

## ADR-026 — Hedef sahipliği: HMAC challenge
- **Karar:** Echo tabanlı doğrulama yerine `HMAC(secret, "apiarena-verify."+challenge)` yanıtı istenir; genel amaçlı echo servisleri doğrulanamaz. Doğrulama 30 gün; URL/sır değişince düşer.
- **Alternatif:** DNS TXT kaydı — daha güçlü ama junior hedef kitle ve yerel demo için ağır.
- **Durum:** Kabul.

## ADR-027 — Webhook imza şeması
- **Karar:** `v1=hex(HMAC-SHA256(secret, "{timestamp}.{deliveryId}.{rawBody}"))`, ±300 sn pencere; event id gövdede ve başlıkta, delivery id her denemede yeni. Sır AES-256-GCM ile şifreli saklanır (HMAC için düz değer gerektiğinden hash yapılamaz).
- **Durum:** Kabul.

## ADR-028 — Örnek alıcı: ham SQL + açık transaction
- **Karar:** `examples/webhook-receiver` EF yerine `Microsoft.Data.SqlClient` ile açık SQL kullanır; idempotency (PK + aynı transaction) ve sürüm koruması öğretici biçimde görünür olsun diye. Bağımsız (merkezi paket yönetimi dışında) proje.
- **Durum:** Kabul.

## ADR-029 — Production Identity key encryption ve proxy sınırı
- **Bağlam:** F6 plaintext persisted DataProtection keys; boş KnownProxies/KnownIPNetworks forwarded headers güven sınırını kaldırıyordu.
- **Karar:** Production external PFX/private key zorunlu; ephemeral keyset, ProtectKeysWithCertificate, eski cert decrypt listesi. Dev/test davranışı korunur. Boş proxy config loopback-only; explicit liste IP olmalı. SQL connection string SqlConnectionStringBuilder ile parse edilir; Trust Server Certificate=yes alias bypass engellenir.
- **Alternatif:** Cloud KMS hesabı yok; DPAPI Linux deployment ile uyumsuz. Her proxy'ye güvenmek kabul edilmez. Eski plaintext keys otomatik re-encrypt edilmiş sayılmaz; fresh DB veya kontrollü geçiş gerekir.
- **Kanıt:** ProductionConfigGuardTests, Stage11 encryptedSecret/cookie tests, Stage17_ProxyTests.
- **Durum:** Kabul; production PFX rotation/restore ayrı release kapısı.

## ADR-030 — Deployment profili ve bounded yerel ölçüm
- **Karar:** Non-root multi-stage .NET 10.0.401/10.0.12 noble, Node 24.21.0 bookworm-slim, Next standalone + static/public paketleme; Caddy 2.11.7-alpine. Read-only extern JSON secret dosyası, env öncelikli. Migration ayrı --migrate job ve DDL rolü; normal startup migrate etmez. Precreated internal network'e ayrı SQL/SMTP relay bağlanır; distinct TLS hostname; DB portu publish edilmez. Production webhook=false/dev exception=false zorlar.
- **Alternatif:** Kubernetes/mikroservis/billing kapsam dışı. Remote private routing/VPN ayrıca gerekir. CI/Docker erişimi olmadan deployment başarısı yazılmaz.
- **Ölçüm:** Ephemeral loopback + disposable SQL, 60 s budget; 3 run, 63 inbound, 12 poll, 20 no-op SQL job/2 processor. CPU/RAM/p95/writes + kontrollü initial queue lag; üretim SLA/maliyeti çıkarılmaz.
- **Kaynak:** Resmi .NET/Node/Next/DataProtection kaynakları DEPLOYMENT.md; MCR tag HTTP200. Caddy [resmi release](https://github.com/caddyserver/caddy/releases/tag/v2.11.7); image digests/container scan Docker ortamında kaydedilecek.
- **Durum:** Kabul; Docker/production smoke Blocked.

## ADR-031 — Lisans ve responsible reporting (Mert, 2026-10-07)
- **Seçenekler:** MIT kısa izin/atıf koşulları; Apache-2.0 daha ayrıntılı koşullar ve açık patent hükümleri; lisans seçmeme dış kullanım iznini belirsiz bırakır. Hukuki garanti değildir.
- **Karar:** Mert bu oturumda MIT seçti; LICENSE Copyright 2026 Mert. Apache-2.0 seçilmedi çünkü sahibi MIT'yi tercih etti.
- **Reporting:** GitHub private vulnerability reporting seçildi; uydurma e-posta yok. Hosted repo/enable/form/notification doğrulanmadı: kanal yayın kapısı.
- **Durum:** Lisans kabul; reporting aktivasyonu Blocked. Push/public yayın yetkisi bu karardan çıkarılmaz.

## ADR-032 — v1 kabulü eksik bağımlılıklarla tamamlandı sayılamaz
- **Bağlam:** STATUS eskiydi; Stage15 backend ve Stage16 account deletion mevcut, editor UI/share/retention/metrics yok. UI 7/30 günlük otomatik silme sözü veriyordu.
- **Karar:** 15/16 InProgress, 17 Blocked. Olmayan editor route linkini kaldır; custom detail mevcut slug route'unda. Retention/sharing metinlerini gerçek duruma çek. ADR-011 7 günlük permanent deletion bir tasarım hedefi, uygulanmış job değil.
- **Alternatif:** Tablo/migration/planı shipped özellik veya kapalı webhook'u public feature olarak sunmak C1/C9/I sözleşmesine aykırı.
- **Durum:** Kabul; eksik kod ve ortam kanıtları RELEASE_CHECKLIST.md.
- **Güncelleme (2026-10-07, devam oturumu):** 15 editör UI ve 16 paylaşım/retention/metrik kodu + testleri tamamlandı (ADR-033…035). Kalan v1 kapıları dış erişime bağlıdır; karar ilkesi geçerliliğini korur.

## ADR-033 — Paylaşım: tek sanitize politikası
- **Karar:** Anonim paylaşım yalnızca `ReportSanitizer.ForPublic` çıktısını döner (proje adı, etiket, kimlikler, Idempotency-Key, event/delivery id, başlık/gövde, e-posta yok). Token `aa_sh_` + 256 bit, yalnızca SHA-256 hash; ≤30 gün, iptal edilebilir; yanıt `X-Robots-Tag: noindex`, IP başına 60/dk. Oturumlu export daha zengin kalabilir.
- **Durum:** Kabul.

## ADR-034 — Retention outbox işi olarak
- **Karar:** Temizlik, worker'ın her saat kuyruğa aldığı lease'li `retention-cleanup` işidir; kategori başına `BatchSize × MaxBatches` sınırı, önce id seçip küçük DELETE'ler. Varsayılanlar 7/30 gün (config). Yedeklerdeki kopyalar yedek saklama süresiyle sınırlıdır.
- **Durum:** Kabul.

## ADR-035 — Metrikler: BCL Meter, exporter opsiyonel
- **Karar:** `System.Diagnostics.Metrics` ile `APIArena` meter; etiketler sabit küçük sözlükler. Exporter bağımlılığı eklenmedi; dağıtım ortamı seçince OpenTelemetry ile bağlanır. Alıcı olmadan "alarm kurulu" denmez.
- **Durum:** Kabul.

## ADR-036 — Renk paleti v2 (proje sahibinin isteği)
- **Bağlam:** C8 "koyu lacivert/füme, mor-mavi vurgu" önerir; Mert 2026-10-07'de paleti beğenmediğini ve tamamen değiştirilmesini istedi.
- **Karar:** Nötr gri tonlar + tek vurgu (koyu teal #0f766e). Varsayılan açık tema; işletim sistemi karanlık mod isterse nötr grafit karanlık tema. Token adları korundu (bileşen değişikliği minimum). Kontrast: axe wcag2a/aa her iki temada ciddi/kritik ihlal 0 (E2E). HTML export da açık palete alındı.
- **Etkisi:** `docs/screenshots` altındaki eski ekran görüntüleri eski paleti gösterir; yayın öncesi yeniden üretilmeli.
- **Durum:** Kabul (C8 renk önerisinden bilinçli sapma).

### ADR-036 güncellemesi — Palet v3 (2026-10-08)
- **Bağlam:** Mert v2'yi (gri + teal) fazla sönük buldu; "canlı, sarı mavi kırmızı gibi" renkler istedi.
- **Karar:** Ana renkler: mavi #2563eb (birincil düğme/bağlantı, beyaz metin 5.2:1), güneş sarısı #facc15 (ikincil düğmeler, vurgular; üstünde koyu metin #1c1917), alev kırmızısı #dc2626 (tehlikeli işlemler). Sıcak krem arka plan #fffbf0, soluk sarı tablo başlıkları; koyu temada gece mavisi. Başlık altında dekoratif mavi/sarı/kırmızı şerit; rapor sayaç kutuları durum rengiyle çerçeveli. Sarı, açık zeminde metin rengi olarak kullanılmaz (kontrast). Durum yine her zaman ikon + kelimeyle verilir (renk yalnızca ikincil ipucu).
- **Doğrulama:** Playwright 9/9; axe wcag2a/aa açık ve koyu temada ciddi/kritik ihlal 0.

### ADR-036 güncellemesi — Palet v3.1 (2026-10-10)
- **Bağlam:** Mert yerel testte krem zemini ve sarımsı tablo başlıklarını beğenmedi; beyaz arka plan ve farklı tablo rengi istedi. Üst menüde "Hesap" en başta olsun istedi.
- **Karar:** Sayfa zemini #ffffff; tablo başlığı/hafif dolgu #eef2f9, hover/aktif menü #dbe5f7, kenarlıklar #dde3ec / #b6c0cf (nötr mavi-gri). Mavi/sarı/kırmızı vurgu renkleri ve koyu tema değişmedi; HTML export zemini de beyaz. Menü sırası: Hesap · Panel · Projeler · Senaryolar · Karşılaştır. Koşu canlı ekranına örnek istemci komutları (PowerShell + bash) eklendi; önceden yalnızca koşu sihirbazında görünüyordu.
