# API Arena — Tehdit modeli

Yöntem: güven sınırı (trust boundary) bazlı STRIDE eşlemesi. Her risk → kontrol → requirement ID (bkz. `ACCEPTANCE.md`). Bu belge "hiç açık yok" iddiası değildir; kalan riskler en altta ve `SECURITY_REVIEW.md`'de.

## 1. Güven sınırları
| # | Sınır | Taraflar | Güven |
|---|---|---|---|
| TB1 | Tarayıcı ↔ Frontend/Control API | Oturumlu kullanıcı, saldırgan site (CSRF) | Cookie + CSRF + Origin |
| TB2 | Kullanıcı uygulaması ↔ Simulator | Herhangi bir istemci, run-key sahibi | Yalnızca run-key; cookie geçersiz |
| TB3 | Control API/Simulator/Worker ↔ DB | İç servisler | Ayrı DB kullanıcısı (prod), private network |
| TB4 | Worker ↔ Webhook hedefi (dış ağ) | Kullanıcının beyan ettiği URL | **Güvenilmez**; SSRF guard + egress |
| TB5 | Public paylaşım ↔ Rapor | Anonim okuyucu | Token hash + expiry + sanitize |
| TB6 | Frontend ↔ kullanıcı içeriği (header/body) | Saldırgan payload | Düz metin render |
| TB7 | Repo/CI ↔ secret'lar | Katkıcılar, fork PR'lar | Secret store, minimum izin |

## 2. Risk tablosu
| Risk | Sınır | Senaryo | Kontrol | Req |
|---|---|---|---|---|
| Kimlik sahteciliği / brute force | TB1 | Parola deneme, credential stuffing | Identity hash, lockout (5 deneme/15 dk), IP rate limit | AUTH-01, AUTH-05 |
| Hesap varlığı sızıntısı | TB1 | register/forgot yanıt farkı | Genel yanıtlar, aynı durum kodu | AUTH-06 |
| CSRF | TB1 | Başka siteden form/fetch | SameSite=Strict, Origin allowlist, antiforgery header | AUTH-03 |
| Oturum çalınması/sürmesi | TB1 | Logout sonrası cookie tekrar kullanımı | Security stamp yenileme + kısa doğrulama aralığı | AUTH-04, AUTH-08 |
| Token kötüye kullanımı | TB1 | Reset/verify token tekrar kullanımı | Identity token providers (kısa ömür, stamp ile tek kullanım), loglarda maske | AUTH-07, AUTH-08, AUTH-11 |
| IDOR | TB1 | Başkasının project/run/report/export/key/share/target ID'si | Tüm sorgular `AccessScope` üzerinden, yabancı kaynak → 404 | OWN-01, OWN-02 |
| Mass assignment | TB1 | ownerId/status alanı göndermek | Request DTO allowlist | OWN-03 |
| Run-key sızıntısı | TB2 | Log, DB, query string, ekran | 256-bit CSPRNG, yalnız SHA-256 hash, bir kez gösterim, header-only, log redaction, TTL/revoke | RUN-04, AUTH-11 |
| Cookie ile simülatör erişimi | TB2 | Kontrol paneli cookie'si simülatöre gider (localhost'ta port paylaşımı) | Simülatör cookie'yi yok sayar; prod'da ayrı host + host-only cookie | SIM-02 |
| Kaynak tüketimi (DoS) | TB2 | Çok istek, büyük gövde, uzun gecikme tutma | 200 istek/koşu, 3 aktif koşu, 64 KiB gövde, iptal edilebilir gecikme, auth başarısızlığı pahalı iş başlatmaz | SIM-06, SIM-10, RUN-05 |
| Sayaç yarışı | TB2/TB3 | Paralel istekle 503/503/200 sırası bozulur | DB atomik UPDATE/MERGE, süreç içi sayaç yok | SIM-03 |
| SSRF | TB4 | Hedef URL ile iç ağ/metadata/DB'ye erişim, DNS rebinding, redirect | URL doğrulama, tüm A/AAAA kontrolü, doğrulanan IP'ye bağlanma, redirect kapalı, egress kısıtı, flag kapalı | WH-01…WH-04, WH-12 |
| Hedef sahipliği | TB4 | Başkasının sunucusuna POST yağdırma | HMAC challenge doğrulaması, rate limit, audit | WH-05 |
| Webhook imza sırrı sızıntısı | TB3/TB4 | DB dökümü | AES-GCM at-rest, anahtar env/secret store'da, masked UI | WH-06 |
| Replay | TB4 | Eski teslimi tekrar göndermek | Timestamp penceresi + event dedup (örnek alıcı) | WH-08, WH-10 |
| Stored XSS | TB6 | Header/body/operation key içinde script | React düz metin, `dangerouslySetInnerHTML` yok, HTML export'ta encode + CSP `default-src 'none'` | SEC-04, REP-06 |
| Paylaşımda veri sızıntısı | TB5 | Public raporda header/body/e-posta | Ayrı sanitize DTO, noindex, `Referrer-Policy: no-referrer`, expiry/revoke | REP-07 |
| Secret sızıntısı (repo/CI) | TB7 | Commit'e anahtar, fork PR'da secret | `.env.example` boş, gitleaks, `pull_request` (target değil), minimum `permissions` | SEC-03, SEC-05, OPS-07 |
| Hata ayrıntısı sızıntısı | TB1/TB2 | Stack trace, iç IP | ProblemDetails, prod'da detay yok, ağ hatası genelleştirilir | SEC-02, WH-04 |
| Özel senaryo kötüye kullanımı | TB2 | Script, sonsuz döngü, header enjeksiyonu, path traversal | Deklaratif şema, adım/gecikme sınırı, header allowlist, route regex | SEC-06 |
| Log enjeksiyonu / PII | TB3 | CRLF'li header değeri | Yapılandırılmış log, header değerleri loglanmaz | AUTH-11 |

## 3. Kalan riskler (kabul edilen / açık)
- Kimlik doğrulama rate limit'i instance içi bellektedir; çoklu instance'ta toplam limit instance sayısıyla çarpılır. Identity lockout DB'dedir.
- Geliştirme ortamında (localhost) cookie'ler port ayırt etmez; simülatör cookie'yi yok sayar ama tarayıcı onu gönderir.
- Production egress kısıtı yalnızca uygulama dışı altyapı ile sağlanabilir; doğrulanmadıkça webhook özelliği production'da kapalı kalır.
- Adapter yanıtları kullanıcı sistemince üretilir; doğruluğu garanti edilemez ("AdapterReported").
- Çoklu instance arasında saat farkı rate-limit değerlendirmesini etkileyebilir (NTP varsayımı, 100 ms tolerans).

## 17 incelemesi — uygulanmış ve planlanan kontroller
TB5 public sharing ve retention/metrics tasarım hedefidir: ShareLinks şeması var, endpoint/sanitize/expiry/revoke testleri yok. V1 release Blocked. Yeni production DataProtection keys external PFX ile şifrelenir; eski plaintext key'ler otomatik çevrilmez, production cert backup/rotation/restore kanıtı yok. Boş forwarded proxy listesi artık loopback-only; explicit IP listesi doğrulanır. Gerçek TLS proxy cookie isolation smoke Docker/hosting olmadan yapılamadı.
