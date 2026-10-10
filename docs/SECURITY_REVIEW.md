# Güvenlik incelemesi (Aşama 11 — MVP)

Tarih: 2026-10-07 · İnceleyen: AI destekli kod incelemesi · Kapsam: commit edilmemiş çalışma ağacı (git geçmişi henüz yok).

> Bu belge "açık yok" beyanı **değildir**. İnceleme kapsamını, kanıtı ve kalan riskleri listeler. Bağımsız bir insan güvenlik incelemesi yapılmamıştır.

## 1. Kapsam ve yöntem
- `THREAT_MODEL.md` risk tablosundaki her satır kod ve test kanıtıyla karşılaştırıldı (aşağıda §3).
- Negatif testler: `backend/tests/APIArena.IntegrationTests/Stage02…Stage11*`, frontend `components.test.tsx`, E2E `e2e/*.spec.ts`.
- Bağımlılık taraması: `dotnet list package --vulnerable --include-transitive`, `npm audit`, `npm audit --omit=dev`.
- Secret taraması: gitleaks 8.30.1 (resmi GitHub release, SHA-256 checksum doğrulandı) `gitleaks dir . --config .gitleaks.toml --redact`. Repo'da commit olmadığı için **git geçmişi taraması yapılamadı**; CI'da `gitleaks git` çalışacak şekilde yapılandırıldı.
- Production konfigürasyonu: `ProductionConfigGuard` birim testleri + Production ortamında entegrasyon testi.
- İncelenmeyenler: canlı hosting, TLS sonlandırma, gerçek SMTP sağlayıcısı, ağ çıkış (egress) politikası, container imajları (Docker yok), sızma testi.

## 2. Bulgular ve sonuçları
| # | Bulgu | Önem / istismar edilebilirlik | Sonuç |
|---|---|---|---|
| F1 | Simülatörde eşzamanlılık sınırı kimlik doğrulamadan **sonra** alınıyordu; geçersiz anahtarlarla sınırsız sayıda (ucuz ama DB'ye giden) doğrulama sorgusu tetiklenebilirdi | Orta / düşük maliyetli DoS yüzeyi | **Düzeltildi:** sınırlayıcı en başa alındı + IP başına dakikalık bütçe (`SimulatorRequestsPerIpPerMinute`, varsayılan 1200), yanıt `429` + `X-Arena-Platform: client-rate-limited`. Test: `Stage11…Simulator_limits_requests_per_client_before_authentication` |
| F2 | Simülatör ve Worker host'larında `IUserDirectory` DI kaydı yoktu (Worker'ın süre dolumu taraması çalışma anında çökerdi) | Yüksek (işlevsel) / güvenlik dışı | **Düzeltildi**, `HostCompositionTests` + factory'lerde `ValidateOnBuild` |
| F3 | `braces ≤3.0.3` (GHSA-vfj7-8cjw-p6xm, high) — `eslint-config-next → @next/eslint-plugin-next → fast-glob → micromatch → braces` | Yüksek (CVSS) / **düşük**: yalnızca geliştirici/CI lint aracında, saldırganın kontrol ettiği glob deseni gerekir; production bağımlılık ağacında yok (`npm audit --omit=dev` → 0) | **Kabul edildi (gerekçeli)**; düzeltilmiş sürüm yayımlanmamış (npm'de en yeni 3.0.3). Takip: yeni sürüm çıkınca `eslint-config-next` güncellenir |
| F4 | gitleaks: `.next/` derleme çıktısındaki Next.js önizleme anahtarları | Bilgi / git-ignored, repoya girmez | Allow-list (yalnızca build klasörleri) — `.gitleaks.toml` |
| F5 | gitleaks: `Factories.cs` `ApiArena:Auth:…PermitsPer… = "1000"` | Yanlış pozitif (yapılandırma anahtarı) | Satır regex'iyle dar kapsamlı allow-list |
| F6 | DataProtection anahtarları DB'de şifresiz saklanıyordu | Orta / DB okuma erişimi | **Yeni production key ring için düzeltildi (17):** mandatory external PFX + ProtectKeysWithCertificate, Stage11 encryptedSecret kanıtı. Eski plaintext keys otomatik çevrilmez; production key backup/restore/rotation doğrulaması yayın kapısı |
| F7 | Kimlik doğrulama rate limit'i instance içi bellekte | Düşük / çoklu instance'ta limit çarpılır; Identity lockout DB'dedir | Kabul (THREAT_MODEL §3) |
| F8 | Geliştirme ortamında cookie'ler port ayırt etmez → tarayıcı kontrol paneli cookie'sini `localhost:5151`'e gönderir | Düşük / yalnızca yerel | Simülatör cookie'yi yok sayar (test: `Stage05…Requests_without_valid_run_key…`); production'da ayrı host zorunlu (`ProductionConfigGuard`) |

Kritik veya yüksek **ve** istismar edilebilir açık bulgu kalmadı; F3 yüksek CVSS ama production'da yok ve istismar koşulu düşük.

## 3. Tehdit modeli eşlemesi (kanıt)
| Risk | Kontrol | Kanıt |
|---|---|---|
| Brute force / enumeration | lockout, rate limit, genel yanıtlar, zamanlama eşitleme | `Stage02…Account_locks…`, `Login_is_rate_limited…`, `Register_and_forgot_do_not_reveal…` |
| CSRF | SameSite=Strict, Origin allowlist, antiforgery header; GET mutasyon yapmaz | `Stage02…Mutation_without_csrf…`, `…foreign_origin…`, `Stage11…Get_requests_never_mutate_state`, `Control_api_has_no_cors_grant…` |
| Oturum sürmesi | sunucu tarafı oturum silme | `Stage02…Session_cookie_is_invalid_immediately_after_logout`, reset sonrası revoke |
| IDOR | `AccessScope`, yabancı kaynak 404 | Stage03/04/06/07_08 "Other_user…" testleri |
| Run-key sızıntısı | 256-bit, hash-only, bir kez, header-only, log yok, revoke/rotate | `Stage04…Key_is_returned_once…`, `Rotating…`, `Stage05…masked`, log taramaları |
| Cookie ile simülatör | cookie yok sayılır | `Stage05…Requests_without_valid_run_key…` |
| Kaynak tüketimi | kota, gövde/URL/header limitleri, iptal edilebilir gecikme, IP bütçesi | `Stage05…quota…`, `…oversized…`, `…Delay_is_released…`, `Stage11…per_client…` |
| Sayaç yarışı | atomik UPDATE/MERGE | `Stage05…parallel…`, `…window_opens_exactly_once…`, restart testi |
| XSS | düz metin render, HTML export encode + CSP | `components.test.tsx`, `Stage07_08…Html_export_is_script_free…` |
| Hata ayrıntısı | ProblemDetails, prod'da iç detay yok | `Stage11…Unhandled_errors_in_production…` |
| Debug yüzeyi | dev mailbox/OpenAPI yalnız Development/Testing | `Stage11…Production_hides_debug_surface…` |
| Secret'lar | `.env.example` yalnızca isim, gitleaks | §2 F4–F5 |
| SSRF / webhook | MVP'de dış istek gönderen özellik **yok**; webhook modülü Aşama 13'te flag kapalı başlar | Aşama 13 testleri |
| Paylaşım sızıntısı | Aşama 16 | Aşama 16 testleri |

## 4. Production başlık ve yapılandırma kontrolü
- API: `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `CSP: default-src 'none'; frame-ancestors 'none'`, `Cache-Control: no-store`, HSTS yalnızca Production + HTTPS (test edildi), `Server` başlığı yok.
- Frontend: nonce'lu CSP (`strict-dynamic`, `object-src 'none'`, `frame-ancestors 'none'`), `X-Frame-Options`, `Referrer-Policy: no-referrer`, `Permissions-Policy`, COOP; `productionBrowserSourceMaps: false` ve `.next/static` altında `.map` dosyası yok (kontrol edildi: 0).
- `dangerouslySetInnerHTML`, `eval`, `localStorage`/`sessionStorage` kullanımı yok (kaynak taraması).
- Bağlantı dizesi: Production'da `TrustServerCertificate=true` reddedilir; DB portu Compose'da yalnızca `127.0.0.1`'e bağlanır.

## 5. Kalan riskler / sonraki adımlar
1. F6 production certificate/CA mount ve key backup/restore/rotation kanıtı (yeni key encryption testli).
2. Git geçmişi secret taraması ilk commit'ten sonra CI'da (`gitleaks git`).
3. Container ve IaC taraması: Docker yok, yapılamadı (Aşama 17'de Dockerfile'lar yazılınca CI'da Trivy önerilir).
4. Bağımsız insan güvenlik incelemesi / sızma testi yapılmadı.
