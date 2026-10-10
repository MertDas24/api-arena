# Kabul matrisi

Durum değerleri: `Planned`, `InProgress`, `Verified` (otomatik test/komut kanıtı var), `Partial`, `Blocked`, `NotRun`.

| ID | Gereksinim | Aşama | Uygulama | Test / kanıt | Durum |
|---|---|---|---|---|---|
| AUTH-01 | Identity ile kayıt/giriş/çıkış/me | 02 | `Api/Endpoints/AuthEndpoints.cs` (/api/auth/*), Identity | `Stage02_AuthTests.Register_verify_login_me_flow…`, `Wrong_password…` | Verified |
| AUTH-02 | HttpOnly cookie, SameSite=Strict, prod Secure + __Host- | 02/17 | `Infrastructure/Identity/IdentitySetup.cs` | `Stage02…Session_cookie_flags…`, `Stage11…Production_hides_debug_surface…` | Verified |
| AUTH-03 | CSRF token + Origin allowlist (unsafe metotlar) | 02 | `Api/Security/CsrfMiddleware.cs`, `/api/auth/csrf` | `Mutation_without_csrf_token…`, `Mutation_from_foreign_origin…`; frontend `api.test.ts` | Verified |
| AUTH-04 | Logout oturumu gerçekten iptal eder | 02 | `Infrastructure/Identity/DbTicketStore.cs` (server-side sessions) | `Session_cookie_is_invalid_immediately_after_logout` | Verified |
| AUTH-05 | Login/register/forgot/reset rate limit + lockout | 02 | Rate limiter `auth`/`auth-sensitive`; Identity lockout 5/15 dk | `Login_is_rate_limited_per_client`, `Account_locks_after_five_failures` | Verified |
| AUTH-06 | Hesap varlığını açığa vurmayan yanıtlar | 02 | Generic register/forgot/login responses, password policy checked before lookup | `Register_and_forgot_do_not_reveal…`, `Wrong_password_and_unknown_account…` | Verified |
| AUTH-07 | E-posta doğrulama token'ı kısa ömürlü, tek kullanımlık | 02 | `TimeAwareTokenProvider` (24 saat), already-verified no-op | `Expired_verification_token_is_rejected…` | Verified |
| AUTH-08 | Reset token kısa ömürlü, tek kullanımlık; reset oturumları düşürür | 02 | Reset token 30 dk, stamp-bound; reset revokes all sessions | `Reset_password_token_is_single_use…`, `Expired_reset_token_is_rejected` | Verified |
| AUTH-09 | Doğrulanmamış hesap koşu başlatamaz | 02/04 | `RunService.StartAsync` (`email_not_verified`), UI uyarısı | `Stage04…Unverified_account_cannot_start_runs` | Verified |
| AUTH-10 | Güvenli return URL | 02 | `frontend/src/lib/url.ts` safeReturnUrl; e-mail links from config only | `url.test.ts` | Verified |
| AUTH-11 | Log redaction (cookie/parola/token/key) | 02 | Body/token never logged; SMTP logs only error type | `Secrets_are_not_written_to_logs` | Verified |
| AUTH-12 | Hesap silme | 16 | `Api/Endpoints/AccountEndpoints.cs` (parola teyidi, cascade) | `Stage16_AccountDeletionTests` | Verified |
| OWN-01 | Merkezi sahiplik kapsamı; yabancı kaynak 404 | 03 | `Application/Access/AccessScope.cs` | `Stage03…User_B_cannot…`, `Stage04…Other_user_cannot_touch_run…` | Verified |
| OWN-02 | İki kullanıcı izolasyon testleri (tüm nested route'lar) | 03+ | AccessScope tüm run alt kaynaklarında | `Stage03`, `Stage04`, `Stage06…Other_user_cannot_read_feed…`, `Stage07_08…Other_user_cannot_read_report_export_or_compare` | Verified |
| OWN-03 | DTO allowlist, mass assignment yok | 03 | Request DTO'ları (`CreateProjectRequest` vb.) | `Stage03…Mass_assignment_fields_are_ignored` | Verified |
| OWN-04 | Pagination, sort allowlist, max page size | 03 | `Common/Paging.cs` | `Stage03…Listing_is_paged_sorted_by_allow_list_and_bounded` | Verified |
| OWN-05 | Proje silme aktif koşuları iptal eder, anahtarları revoke eder | 03 | `ProjectService.DeleteAsync` (soft delete + applock) | `Stage04…Deleting_a_project_cancels_active_runs_and_revokes_keys` | Verified |
| RUN-01 | Sürümlü, immutable yerleşik senaryolar | 04 | `Domain/Scenarios/ScenarioDefinitions.cs`, `ScenarioSeeder` (immutable) | `Stage04…Catalog_lists_three_versioned_builtins…` | Verified |
| RUN-02 | State machine; eşzamanlı start/finish/cancel idempotent | 04 | `RunService` koşullu UPDATE + `sp_getapplock` | `Stage04…Concurrent_starts…`, `Finish_and_cancel_race…` | Verified |
| RUN-03 | Konfigürasyon/evaluator/seed snapshot | 04 | `Run.ConfigSnapshotJson`, `EvaluatorVersion`, `Seed` | `Stage04…New_scenario_version_does_not_change_existing_run_snapshot` | Verified |
| RUN-04 | Run-key 256-bit, hash-only, bir kez gösterim, rotate/revoke, TTL | 04 | `Domain/Runs/RunRules.cs` (RunKeys), `RunService.CreateKeyAsync` | `Stage04…Key_is_returned_once…`, `Rotating_a_key…`, `Stage05…Requests_without_valid_run_key…` | Verified |
| RUN-05 | Aktif koşu (3) ve günlük kota atomik | 04 | `RunService.StartAsync` | `Stage04…Active_run_quota_holds_under_parallel_starts`, `Daily_quota_is_enforced` | Verified |
| RUN-06 | Süre dolumu (lazy + worker), restart sonrası toparlanma | 04 | Lazy expiry + `Worker/RunExpiryService` | `Stage04…Run_expires_after_ttl_lazily_and_by_sweep` | Verified |
| RUN-07 | afterSequence olay akışı, kararlı sıra, duplicate yok | 06 | `ObservationService.FeedAsync`, `frontend/src/lib/feed.ts` | `Stage06…Reconnecting_with_after_sequence…`, `feed.test.ts` | Verified |
| RUN-08 | Maskeli gözlem; body kaydı varsayılan kapalı | 06 | `Domain/Privacy/Masking.cs`; `Project.CaptureBodies` varsayılan false | `Stage05…Recorded_headers_and_bodies_are_masked`, `Stage06…Observation_list_filter_and_detail_are_masked` | Verified |
| SIM-01 | Ayrı host; sınırlı route kataloğu | 05 | `APIArena.Simulator` (:5151), run başına tek fixture route | `Stage05…Unknown_route_and_oversized_body…` | Verified |
| SIM-02 | Run-key gövde okunmadan önce; cookie yetmez | 05 | `SimulatorEndpoints.Handle` (şekil→hash→route→body) | `Stage05…Requests_without_valid_run_key…` (cookie dahil) | Verified |
| SIM-03 | retry-503-v1 eşzamanlılıkta deterministik | 05 | `RunAtomics` (UPDATE/MERGE), `ScenarioEngine` | `Stage05…Retry503_is_deterministic_under_parallel_requests` | Verified |
| SIM-04 | rate-limit-v1 (429 + Retry-After: 2) | 05 | `ScenarioEngine.RateLimit`, atomik pencere | `Stage05…RateLimit_honours…`, `RateLimit_window_opens_exactly_once…` | Verified |
| SIM-05 | timeout-v1 iptal edilebilir 3 s gecikme, kopma gözlemi | 05 | `DelayWhileRunning` (TimeProvider, iptal) | `Stage05…Timeout_delay_completes…` (Kestrel), `Delay_is_released_when_run_is_cancelled` | Verified |
| SIM-06 | Gövde 64 KiB / URL 2 KiB / header 16 KiB | 05 | Kestrel limitleri + `ReadBodyAsync` + URL kontrolü | `Stage05…Unknown_route_and_oversized_body…`, `Oversized_headers_are_rejected_by_kestrel` | Verified |
| SIM-07 | Bitmiş/süresi dolmuş koşu ve revoke edilmiş anahtar reddi | 05 | `AllocateInboundAsync` durum/süre kontrolü | `Stage05…Not_started_finished_and_expired_runs…` | Verified |
| SIM-08 | Planned/Sent/ClientDisconnected/Unknown sonuçları | 05 | `DeliveryOutcome`; finalizasyonda Planned→Unknown | `Stage05` (ClientDisconnected/Sent/Rejected), `EvaluatorTests.Unconfirmed_503…` | Verified |
| SIM-09 | Proje bazlı simülatör CORS, credential yok | 05 | `ApplyCors`/`Preflight` | `Stage05…Cors_allows_only_listed_origins_without_credentials` | Verified |
| SIM-10 | Koşu başına 200 istek kotası; platform yanıtı ayrı | 05 | `AllocateInboundAsync` + `X-Arena-Platform` | `Stage05…Run_quota_is_enforced…`, `Stage07_08…Quota_limited_run_is_incomplete_not_failed` | Verified |
| SIM-11 | Özel senaryo yürütme | 15 | `CustomScenarioCompiler`, `CustomScenarioService`, `ScenarioEngine`; editör `frontend/src/components/custom-scenario-editor.tsx` | `Stage15_CustomScenarioTests` (preview==run, immutable sürüm, gizlilik); E2E `v1-features` editör akışı + 360 px + axe | Verified |
| REP-01 | Deterministik, sürümlü evaluator; kanıtlı kontroller | 07 | `Domain/Evaluation/Evaluator.cs` | `EvaluatorTests` (33 unit), `Stage07_08…Good_client_report_has_evidence_for_every_pass` | Verified |
| REP-02 | Inconclusive semantiği; platform eksikliği başarıya dönmez | 07 | Evaluator absence-based → Inconclusive | `EvaluatorTests.Incomplete_runs…`, `Stage07_08…Cancelled_empty_run…`, `Broken_definition…` | Verified |
| REP-03 | Immutable snapshot, idempotent finalizasyon | 07 | `RunFinalizer` + unique `ReportSnapshots.RunId` | `Stage07_08…Finalization_is_idempotent_and_report_is_immutable` | Verified |
| REP-04 | Rapor ekranı: sayılar, kapsam, timeline, öneriler, "neyi doğrulayamadık" | 08 | `frontend/src/components/report-view.tsx`, rapor sayfası | E2E `main-flow.spec.ts`, `components.test.tsx` | Verified |
| REP-05 | Karşılaştırma + sürüm uyarıları | 08 | `ReportService.CompareAsync`, `/compare` | `Stage07_08…Compare_warns…`, `Bad_client_report_differs…` | Verified |
| REP-06 | JSON/HTML export: maskeli, script yok, sahiplik, boyut limiti | 08 | `ReportService.ExportAsync/RenderHtml` | `Stage07_08…Html_export_is_script_free…`; E2E indirme | Verified |
| REP-07 | Salt okunur süreli paylaşım | 16 | `Application/Reports/ShareService.cs` (hash token, expiry ≤30 g, revoke, `ReportSanitizer`), `/api/public/shares/{token}` (noindex, rate limit), `/share/[token]` | `Stage16…Public_share_is_sanitised…`, `…revoke_expiry_and_deletion`, `…Account_deletion_kills_shares`; E2E paylaş→anonim gör→iptal→404 | Verified |
| WH-01 | Webhook feature flag varsayılan kapalı | 13 | `ApiArena:Webhooks:Enabled=false` varsayılan; `WebhookEndpoints` flag kapalıyken map edilmez; worker handler kaydı flag'e bağlı | `Stage13_14…Webhook_module_is_unreachable_when_flag_is_off` | Verified |
| WH-02 | URL doğrulama (şema, userinfo, fragment, port, IP gösterimleri) | 13 | `Domain/Outbound/NetworkPolicy.cs` UrlPolicy (+ her çağrıda yeniden doğrulama) | `OutboundPolicyTests.Url_policy` (27 vaka), `Stage13_Safe…Url_is_revalidated…`, `Stage13_14…Unsafe_target_urls…` | Verified |
| WH-03 | Tüm A/AAAA kontrolü + doğrulanan IP'ye bağlanma | 13 | IpPolicy + `SafeHttpClient.ConnectAsync` (tüm A/AAAA, doğrulanan IP'ye bağlanma) | `OutboundPolicyTests` (IP sınıfları, mixed), `Stage13_Safe…blocked_before_connecting`, `…Dns_rebinding…`, `…exactly_the_validated_address` | Verified |
| WH-04 | Redirect kapalı; boyut/zaman sınırı; hata sızdırmama | 13 | Redirect kapalı, 64 KiB, 10 sn, hata kodları | `Stage13_Safe…Redirects_are_not_followed`, `…Oversized_responses_and_slow_targets…`, `…Header_injection…` | Verified |
| WH-05 | HMAC challenge ile hedef doğrulama | 13 | `VerifyTargetHandler` HMAC challenge, rate limit, audit, URL değişiminde düşme | `Stage13_14…Verification_requires_hmac_proof…` | Verified |
| WH-06 | İmza sırrı AES-GCM at rest, masked UI | 13 | `AesGcmSecretProtector` (anahtar config'te, KeyId, rotasyon), masked UI | `OutboundPolicyTests.Aes_gcm…`, `Stage13_14…Secret_is_shown_once_encrypted_at_rest…` | Verified |
| WH-07 | Outbox lease/claim/retry, at-least-once | 13 | `Jobs/OutboxProcessor.cs` (UPDLOCK/READPAST lease, recovery), at-least-once | `Stage13_14…Outbox_claims_are_exclusive…expired_leases_are_recovered` | Verified |
| WH-08 | Teslim imzası, event/delivery ID, timestamp | 14 | `WebhookSignature` (v1 HMAC, timestamp, event/delivery id) | `OutboundPolicyTests.Signature…`, alıcı testleri | Verified |
| WH-09 | duplicate/out-of-order senaryoları | 14 | `WebhookRunPlanner`, `duplicate-webhook-v1`, `out-of-order-webhook-v1` | `Stage13_14…Duplicate_webhook…`, `…Out_of_order…`, `…Failed_delivery_is_retried…`, `…Cancelling_a_run…` | Verified |
| WH-10 | Örnek alıcı: dedup, version guard, paralel duplicate | 14 | `examples/webhook-receiver` (PK + transaction, version guard, replay) | `Stage13_14…Sample_receiver_rejects_forgeries_and_applies_parallel_duplicates_once`, naive vs correct raporları | Verified |
| WH-11 | Adapter kanıtı "AdapterReported" | 14 | `FinalizeWebhookRunHandler` (aynı origin, nonce/run bağlama, katı şema), EvidenceKind.AdapterReported | `Stage13_14…correct_receiver…adapter_evidence`, `…Without_adapter…inconclusive` | Verified |
| WH-12 | Production egress kısıtı (infra) | 13/17 | `docs/runbooks/EGRESS.md`, `ProductionConfigGuard` (EgressRestrictionAttested) | Hosting yok — ağ katmanı kısıtı uygulanmadı/doğrulanmadı | Blocked |
| SEC-01 | Güvenlik başlıkları (CSP, nosniff, Referrer-Policy, frame-ancestors, HSTS prod) | 01/11 | `Infrastructure/Web/WebPipeline.cs` SecurityHeaders; `frontend/src/proxy.ts` + `next.config.ts` | `Stage01…security_headers`, `Stage11…Production_hides_debug_surface…` (HSTS), elle CSP kontrolü | Verified |
| SEC-02 | ProblemDetails + correlation ID; prod'da stack trace yok | 01 | ProblemDetails + correlation; prod'da iç detay yok | `Stage11…Unhandled_errors_in_production…` | Verified |
| SEC-03 | Repo'da secret yok; .env.example boş | 01/17 | `.gitignore`, `.dockerignore`, `.env.example`, `.gitleaks.toml` | 2026-10-07 gitleaks 8.30.1 checksum doğrulandı, working tree 0; geçmiş yok | Verified (working tree) |
| SEC-04 | XSS: düz metin render | 06/08 | React düz metin, `dangerouslySetInnerHTML` yok | `components.test.tsx` (XSS), `Stage07_08` HTML export | Verified |
| SEC-05 | Dependency + secret taraması | 11/12 | `scripts/security-scan.sh`, CI `security` işi | dotnet vulnerable: 0; npm prod: 0; gitleaks: 0 — CI remote çalıştırılmadı | Partial |
| SEC-06 | Özel senaryo sandbox kuralları | 15 | `CustomScenarioCompiler` (katı şema, limitler), `/import` (32 KiB, bilinmeyen alan reddi) | `Stage15_CustomScenarioTests` (14 geçersiz tanım, import saldırıları, text/plain + CSP); E2E geçersiz route hatası | Verified |
| SEC-07 | Export dosya adı sunucu üretimli | 08 | Sunucu üretimli dosya adı | `Stage07_08…Html_export…` (regex), E2E | Verified |
| OPS-01 | Liveness DB'den bağımsız; readiness DB'ye bağlı | 01 | `Infrastructure/Web/HealthEndpoints.cs`, `/api/health` | `Liveness_is_independent_of_database_but_readiness_is_not`, `Liveness_and_readiness…` | Verified |
| OPS-02 | Konfigürasyon doğrulaması fail-fast | 01 | `Options/ProductionConfigGuard.cs`, DataAnnotations `ValidateOnStart` | `ProductionConfigGuardTests` (unit); dev run with wrong env failed fast | Verified |
| OPS-03 | Migration boş DB'de uygulanır | 01 | `Persistence/Migrations/*_Initial` | `Migrations_are_applied_to_empty_test_database`; `dotnet ef database update` on empty ApiArena_Dev | Verified |
| OPS-04 | Retention temizliği idempotent, leased, batch sınırlı | 16 | `Application/Ops/RetentionService.cs`, `Jobs/OpsJobs.cs` (lease'li outbox işi, batch sınırlı) | `Stage16…Retention_is_bounded_idempotent…` (7/30 gün, silinen proje, ikinci tur 0) | Verified |
| OPS-05 | Düşük kardinaliteli metrikler | 16 | `Application/Ops/ArenaMetrics.cs` (APIArena meter), worker gauge örnekleme; `runbooks/OPERATIONS.md` alarm tanımları | `Stage16…Metrics_use_only_low_cardinality_tags`; **alıcı/alert bağlı değil** | Partial |
| OPS-06 | Yedek/restore tatbikatı | 16/17 | `scripts/restore-drill.ps1` | 3 migration→CHECKSUM→VERIFYONLY→yeni DB restore→CHECKDB→marker; prod encrypted backup/PFX restore ayrı blocked | Verified (local) |
| OPS-07 | CI workflow | 12 | `.github/workflows/ci.yml`, `scripts/ci-sql.sh` | actionlint 1.7.12 temiz; remote run doğrulanmadı | Partial |
| OPS-08 | Production container/config/runbook | 17 | `infra/*`, secret JSON, PFX encryption, `--migrate`, `runbooks/DEPLOYMENT.md` | YAML/static checks, production guard + Stage17_ProxyTests, migration job iki kez; Docker/hosting/TLS/scan yok | Partial |
| OPS-09 | Sınırlı yerel yük testi | 17 | `Stage17_BoundedLoadTests`, `scripts/load-test.ps1` | 3 run/63 inbound/12 poll/20 job, SQL writes+CPU/RAM/p95/fixture lag; `LOAD_SAMPLE.json` | Verified (local) |
| UX-01 | Türkçe merkezi metinler | 10 | `frontend/src/lib/i18n/tr.ts` | Kod incelemesi | Verified |
| UX-02 | "Şimdi ne yapmalıyım?" + 3 adımlı kurulum | 10 | `next-step.tsx`, 3 adımlı sihirbaz | `components.test.tsx` (nextStepFor), E2E main flow | Verified |
| UX-03 | empty/loading/error/unauthorized/expired/quota durumları | 10 | `ErrorState`, `EmptyState`, `RequireAuth`, quota/expired uyarıları | E2E (`anonymous visitor…`), bileşen testleri | Verified |
| UX-04 | Erişilebilirlik (klavye, label, renk dışı durum, reduced motion) | 10 | Durum ikon+metin, label'lar, skip link, reduced-motion CSS | E2E axe (wcag2a/aa, serious+critical = 0), klavye testi | Verified |
| UX-05 | 360/390/768/1440 px | 10 | Responsive düzen, kaydırılabilir tablolar | E2E 9 ekran × 360/390/768/1440 taşma yok | Verified |
| UX-06 | Secret bir kez gösterim + kopyalama | 10 | `secret-once.tsx` | `components.test.tsx`, E2E | Verified |
| UX-07 | Beklenen simülasyon hatası ≠ platform hatası | 10 | `HttpStatus` etiketi, `X-Arena-Platform` | E2E ("beklenen simülasyon"), `Stage05` platform başlıkları | Verified |
| DOC-01 | docs seti (spec, mimari, tehdit, ADR, kabul, durum, devir) | 00 | `docs/*` | Files exist | Verified |
| DOC-02 | README kurulum (Windows/macOS/Linux) | 01/17 | `README.md` | `scripts/test.ps1` + fresh DB migrate (Windows native); Docker yolu doğrulanamadı | Partial |
| DOC-03 | Örnek istemci README | 09 | `examples/dotnet-client/README.md` | `Stage09_ExampleClientTests` | Verified |
| DOC-04 | SECURITY_REVIEW.md | 11 | `docs/SECURITY_REVIEW.md`, `SECURITY.md` | İnceleme belgesi | Verified |
| DOC-05 | Runbook, PORTFOLIO.md, release checklist | 16/17 | `runbooks/DEPLOYMENT.md`, `PORTFOLIO.md`, `MERT_ARCHITECTURE.md`, `RELEASE_CHECKLIST.md`, `LOAD_TEST.md` | Dosyalar + gerçek E2E screenshots; v1 kapıları blocked gerekçeli | Verified (documents) |
