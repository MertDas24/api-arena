# Test sonuçları

Her kayıt: tarih, exact komut, exit code/sonuç. Çalıştırılmayan kontroller nedeniyle birlikte listelenir. Uydurma sayı yok.

## Aşama 00 — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet --list-sdks` | 8.0.419, 10.0.401 |
| `dotnet --list-runtimes` | Microsoft.AspNetCore.App 10.0.12 |
| `node --version` / `npm --version` | v24.21.0 / 11.19.0 |
| `docker --version` | Bulunamadı (Docker kurulu değil) |
| SQL bağlantısı (`Server=localhost;Integrated Security=true`) | SQL Server 2025 Developer 17.0.1000.7, sysadmin=1 |
| `npm view next/react/tailwindcss version` | 16.4.0 / 19.3.0 / 4.3.3 |
| .NET destek sayfası (WebFetch) | .NET 10 LTS, 10.0.12, EOS 2028-11-14 |
| Next.js support policy (WebFetch) | 16.x Active LTS |

Aşama 00 kod içermez; otomatik test yoktur.

## Aşama 01 — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet build backend/APIArena.sln` | exit 0, 0 uyarı, 0 hata |
| `dotnet ef migrations add Initial` + `dotnet ef database update` (boş `ApiArena_Dev`) | exit 0, `20261007145107_Initial` uygulandı |
| `dotnet test backend/APIArena.sln` | UnitTests 8/8 geçti; IntegrationTests 6/6 geçti (gerçek SQL Server, test başına `ApiArena_Test_<guid>`) |
| `npx tsc --noEmit` / `npx eslint .` | exit 0 / exit 0 |
| `npx vitest run` | 2 dosya, 15 test geçti |
| `npx next build` | exit 0, tüm sayfalar dinamik (ƒ), Proxy aktif |
| Elle: API + `next dev`, `GET http://localhost:3000/api/health` | 200 `{"status":"Healthy",…,"checks":[{"name":"database","status":"Healthy"}]}` |
| Elle: `GET http://localhost:3000/login` başlıkları | CSP nonce + `strict-dynamic`, `X-Frame-Options: DENY`; HTML'de nonce mevcut |
| Elle: `dotnet run` ortam ayarı olmadan | ProductionConfigGuard ile başlatma reddedildi (exit 82) — fail-fast doğrulandı |
| **Çalıştırılmadı:** `docker compose up` | Docker kurulu değil; Compose dosyası yazıldı ama doğrulanmadı |

## Aşama 02 — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet test backend/tests/APIArena.IntegrationTests` | 21/21 geçti (Stage01 6 + Stage02 15) |
| Elle: Next proxy üzerinden register → dev mailbox → verify → login → me | Tümü 2xx; `Set-Cookie: aa_session=…; path=/; samesite=strict; httponly` |
| `npx eslint .` / `npx tsc --noEmit` / `npx vitest run` | exit 0 / exit 0 / 15 test geçti |

## Aşama 03–10 — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet ef migrations add CoreModel` + `database update` | exit 0, `20261007151549_CoreModel` uygulandı |
| `dotnet test backend/APIArena.sln` | UnitTests 33/33; IntegrationTests 79/79 (3 ardışık koşu yeşil) |
| — ilk denemelerde başarısız olan ve düzeltilen | `Delay_is_released…` (finalizer Unknown işaretlemesi, ADR-021); `Timeout_naive_vs_resilient` (paylaşılan test DB'sinde global süre dolumu taraması diğer testlerin koşularını etkiliyordu — test saati izole edildi, ADR-024); Simülatör/Worker DI'da `IUserDirectory` eksikti — Development'ta `ValidateOnBuild` yakaladı, düzeltildi ve `HostCompositionTests` eklendi |
| `npx tsc --noEmit` / `npx eslint .` | exit 0 / exit 0 |
| `npx vitest run` | 4 dosya, 29 test geçti |
| `scripts/e2e.ps1` → `npx playwright test` (Chromium, gerçek API+simülatör+`next start`, DB `ApiArena_E2E`) | 5/5 geçti: ana akış (kayıt→doğrulama→proje→sihirbaz→anahtar→başlat→trafik→canlı→bitir→rapor 6 Passed→HTML indir→anahtar 401), anonim yönlendirme, simülatör platform hatası, 9 ekran × 360/390/768/1440 yatay taşma yok + axe wcag2a/aa ciddi/kritik ihlal 0, klavye skip link |
| — E2E sırasında bulunan ve düzeltilen | 360 px'te tablo içi `sr-only` metinlerin sayfayı taşırması (sarmalayıcıya `relative`); beyaz metin + vurgu rengi kontrastı 4.0:1 → 5.1:1; kaydırılabilir bölgeler klavyeyle odaklanamıyordu (`tabIndex=0`) |
| Görsel QA | Playwright tam sayfa ekran görüntüleri (`frontend/test-results/**/screens/*.png`, git'e eklenmez); 1440 canlı ekran ve 390 panel incelendi |

## Aşama 11–12 — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet list backend/APIArena.sln package --vulnerable --include-transitive` | 9 projede açık paket yok |
| `npm audit` | 5 high — hepsi `braces` (GHSA-vfj7-8cjw-p6xm) zinciri, yalnızca `eslint-config-next` (dev) |
| `npm audit --omit=dev` | 0 vulnerability |
| `gitleaks dir . --config .gitleaks.toml --redact` (8.30.1, checksum doğrulandı) | ilk çalıştırma 13 bulgu (12 `.next/` build çıktısı, 1 yapılandırma anahtarı yanlış pozitif) → gerekçeli dar allow-list → 0 |
| `dotnet test` (Stage11 dahil) | Unit 33/33, Integration 84/84 |
| `actionlint .github/workflows/ci.yml` (1.7.12, checksum doğrulandı) | exit 0 |
| `bash -n scripts/*.sh` | 7 betik sözdizimi geçerli |
| `scripts/test.ps1` | exit 0 ("All checks passed.") |
| `scripts/db-migrate.ps1` (boş `ApiArena_FreshCheck`) | 2 migration uygulandı; DB sonra silindi |
| `--seed-demo` + `scripts/demo.ps1 -Scenario retry-503` (canlı yığın) | naive 1 istek 503; resilient 503→503→200; rapor/karşılaştırma linkleri üretildi |
| **Çalıştırılmadı:** GitHub Actions remote run | Repo push edilmedi (remote ve yetki yok) |
| **Çalıştırılmadı:** `docker compose up` / Docker fresh setup | Docker yok |

## Aşama 13–14 — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet ef migrations add V1WebhooksSharingAudit` + `database update` | exit 0 |
| `dotnet test tests/APIArena.UnitTests` | 92/92 (URL politikası 27 vaka, 24 özel/ayrılmış IP, imza, AES-GCM rotasyonu dahil) — ilk çalıştırmada 2 vaka yalnızca farklı hata koduyla reddediliyordu; kontrol sırası düzeltildi |
| `dotnet test --filter Stage13_Safe` | 18/18 (sahte DNS + yerel Kestrel; gerçek dış siteye istek yok) |
| `dotnet test --filter Stage13_14` | 16/16 (API + worker eşdeğeri + gerçek örnek alıcı kodu) |
| `dotnet test backend/APIArena.sln` | Unit 92/92, Integration 118/118 |
| `scripts/dev-secrets.ps1 -Webhooks -Docker` / `dev-secrets.sh --webhooks --docker` (izole kopyada) | anahtarlar üretildi, değer yazdırılmadı; `bash -n` geçerli |
| `npx tsc --noEmit` / `npx eslint .` | exit 0 / exit 0 |
| **Çalıştırılmadı:** production egress kısıtı | Hosting yok; `docs/runbooks/EGRESS.md` hazırlandı, doğrulanmadı |

## Aşama 17 — release hazırlığı (2026-10-07)

| Exact komut / kontrol | Gerçek sonuç |
|---|---|
| `dotnet build backend/APIArena.sln -c Release -nologo -v q` | exit 0; full fresh compile'da mevcut CS9113/CS8826/xUnit1051 uyarıları görüldü. Sandbox ilk denemede user NuGet.Config erişimini reddetti; izinli yerel çalıştırma geçti |
| `dotnet test backend/APIArena.sln -c Release -nologo -v q --logger 'console;verbosity=quiet'` | exit 0, 94 unit + 139 real SQL integration, skip 0; Stage15 backend, Stage16 account deletion, Stage17 load/proxy dahil |
| `dotnet build backend/src/APIArena.Api -c Release -nologo -v q` + `./scripts/migration-job-smoke.ps1` | Son migration fail-fast seed kodu build exit0; fresh unique DB'de --migrate iki kez + 3 migration/core seed, MIGRATION_JOB_PASSED; DB silindi |
| `npm run lint`; `npm run typecheck`; `npm test` (frontend) | Her biri exit0, 4 dosya / 29 test; Vitest config native loader geleceğe dönük informational warning |
| `./scripts/e2e.ps1` (standalone final frontend) | Backend build + E2E migrations + `npm run build` + Playwright; 5/5, skip0. 9 mevcut ekran × 360/390/768/1440 overflow yok, 390/1440 axe serious/critical0, keyboard skip. İlk `next start` uyarısı standalone server + asset packaging ile düzeltildi |
| `npx playwright test e2e/main-flow.spec.ts` | 3/3; gerçek 503→503→200 masked live ve 6 Passed rapor screenshots alındı; son full E2E bunları yeniden üretti |
| `$env:APIARENA_LOAD_RESULT = Join-Path (Get-Location) 'artifacts/bounded-load.json'; dotnet test backend/tests/APIArena.IntegrationTests -c Release --filter Stage17 -nologo --logger 'console;verbosity=normal' -v q` (load testi eklenmiş, proxy testi henüz yokken) | 1/1 bounded load; json docs/LOAD_SAMPLE.json'a kopyalandı; repeat script scripts/load-test.ps1. Gerçek hedef loopback Kestrel + unique test SQL; 3 run/63 inbound/12 poll/20 job, duplicate0. CPU/RAM/p95/DB writes/controlled lag docs/LOAD_TEST.md |
| `./scripts/restore-drill.ps1` | İlk backup path separator hatası düzeltildi; son exit0: 3 migration fresh→COPY_ONLY/CHECKSUM backup→VERIFYONLY→yeni DB restore→CHECKDB→history/data marker. İki disposable DB silindi; synthetic backup SQL server default Backup dizininde korundu |
| `sqlcmd -S localhost -E -C -b -d ApiArena_Dev -Q 'SET NOCOUNT ON; SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId;'` (E2E için de aynı) | exit0; Initial, CoreModel, V1WebhooksSharingAudit her ikisinde mevcut; prod uygulanmadı |
| `dotnet list backend/APIArena.sln package --vulnerable --include-transitive` | exit0, 10 projede vulnerability yok |
| `npm audit --omit=dev` | exit0, 0 vulnerability |
| `npm audit --audit-level=critical` | exit0, dev chain braces 5 high (F3 devam); prod0. Force downgrade yapılmadı |
| `./artifacts/tools/gitleaks.exe dir . --config .gitleaks.toml --redact --no-banner --max-target-megabytes 5 --report-format json --report-path artifacts/gitleaks-report.json` | 8.30.1 GitHub release SHA256 checksum doğrulandı; ilk iki bulgu yalnız indirilen tool README örnekleri; üretilmiş README kaldırıldı, suppression eklenmedi. Son exit0 / 0. Tool exe/zip 5MB üstü binary atlandı; source scan yapıldı. Git geçmişi/commit yok |
| `node -e` js-yaml parse + private service port/resource static assertions (frontend) | exit0; Compose YAML parse ve api/simulator/frontend ports yok + read_only/mem/cpu kontrolleri geçti. Docker compose semantic/runtime doğrulaması değildir |
| Resmi MCR `/v2/dotnet/{sdk,aspnet}/manifests/{10.0.401-noble,10.0.12-noble}` HEAD (`Invoke-WebRequest -UseBasicParsing`) | Her tag HTTP200; image build/pull/scan henüz yok |
| `git diff --check` | exit0 ancak tüm kaynak untracked: tam diff kontrolü/commit kanıtı değildir |
| Visual QA | Gerçek 1280 live/report ve 1440 live/390 dashboard PNG'leri incelendi; secrets yok, synthetic example.test hesap, auth masked. README/PORTFOLIO screenshots docs/screenshots'ta |

Başarısız ara denemeler gizlenmedi: proxy testi eksik namespace derlemesi düzeltildi; migration smoke yanlış all-template=3 varsayımı core slug=3 olarak düzeltildi (webhook katalog row'ları da seed ediliyor). Product testleri skip edilmedi. Restore default path trailing slash düzeltildi. Gitleaks suppression genişletilmedi.

**Blocked/NotRun:** Docker fresh build/pull/runtime/CA/TLS/private network/container scan; production DB/PFX encrypted backup + key restore/rotation; GitHub remote CI/private reporting; real hosting/SMTP/domain/public smoke; custom editor UI/share/retention/metrics kabul testleri (kod eksik); webhook gönderim sonrası process kill/restart fixture ve webhook UI/mobile/a11y. Full scripts/test.ps1 yeniden tek komutla çalıştırılmadı; karşılık gelen build/test/lint/typecheck/Vitest/Next kontrolleri ayrı çalıştırıldı. 18–19 billing ve gerçek ödeme seçilmedi (NotApplicable).

Son checkpoint doğrulaması: Release test takımı tekrar exit0 (94 unit / 139 SQL integration, skip0); final UI standalone E2E 5/5. docs/RELEASE_SOURCE_SHA256.json içindeki 216 source/config hash'i Get-FileHash -Algorithm SHA256 ile eşleşti. Tüm scripts/*.ps1 PowerShell Language.Parser ile sözdizimi kontrolünden geçti. Son gitleaks working tree exit0/0 bulgu; YAML/static confinement kontrolleri exit0. Bunlar Docker runtime veya public deployment kanıtı değildir.

## Aşama 15–16 (devam oturumu) — 2026-10-07
| Komut | Sonuç |
|---|---|
| `dotnet test --filter Stage15` | 18/18 (14 geçersiz tanım, import saldırıları, preview==run, sürüm, gizlilik) |
| `dotnet test --filter Stage16_Share` | 5/5 — ilk denemede 2 test, sahte saat atlamasıyla oturum zaman aşımına uğradığı için 401 verdi (ürün doğru); test yeniden giriş yapacak şekilde düzeltildi |
| `dotnet test --filter Crash_after_send` | 1/1 (gönderim sonrası/kayıt öncesi çökme durum fixture'ı) |
| `dotnet test backend/APIArena.sln` | Unit 94/94, Integration 145/145 (2 ardışık tam koşu yeşil) |
| — bulunan ve düzeltilen | `Stage11…Production_hides_debug_surface` paylaşılan DB'deki DataProtection anahtar halkasına bağlı sıra-bağımlı bir test idi → kendi izole veritabanında çalışacak şekilde düzeltildi |
| `scripts/test.ps1` | exit 0 ("All checks passed.") |
| `npx playwright test` (standalone build, gerçek API+simülatör) | 8/8 — v1: editör (önizleme, sunucu doğrulama hatası, kaydetme, 360 px, axe), paylaşım (anonim görüntüleme, noindex, iptal→404), webhook kapalı ekranı (360 px, axe) |
| — E2E'nin bulduğu ve düzeltilen | editördeki gizli dosya girişinin etiketi yoktu (axe `label`); webhook ekranında uzun ortam değişkeni 360 px'te 7 px taşıyordu |
| **Çalıştırılmadı** | Metrik exporter/alarm alıcısı (yok); production yedek/anahtar restore; Docker/hosting |

## 2026-10-08 — Palet v3 + PowerShell 5.1 düzeltmeleri
- `npx tsc --noEmit`, `npx eslint src --max-warnings=0`: temiz.
- Vitest: 29/29 geçti.
- Playwright (izole sunucular, ApiArena_E2E, production build): 9/9 geçti (açık + koyu tema axe dahil).
- Backend unit: 94/94 geçti. Backend derlemesi 0 hata.
- Not: Playwright'ı açık geliştirme sunucularına karşı çalıştırmak (reuseExistingServer) kayıt/doğrulama hız sınırına (15 dk pencere) takılır; bu ilk denemede 2–3 testin düşmesine yol açtı. Testler izole sunucularla çalıştırılmalı.
- Bulunan küçük hata düzeltildi: e-posta doğrulama sayfası 429'u "bağlantı geçersiz" diye gösteriyordu; artık "çok fazla deneme, bağlantınız hâlâ geçerli" + Tekrar dene gösterir.

## Public yayın öncesi kontrol — 2026-10-10
| Komut | Sonuç |
|---|---|
| Elle uçtan uca test (Mert): kayıt → doğrulama → proje → koşu → `examples/dotnet-client` → canlı trafik → rapor → karşılaştır → JSON/HTML export → paylaşım → hesap silme | Çalışıyor |
| `./scripts/demo.ps1 -Scenario retry-503` | naive: 503'te durdu; resilient: 503 → 503 → 200 |
| `./scripts/test.ps1` | exit 0 · Unit 94/94 · Integration 145/145 · Vitest 29/29 · lint/typecheck/build temiz |
| `gitleaks git . --staged --config .gitleaks.toml --redact` (ilk commit adayı, 273 dosya) | no leaks found |
| `dotnet list backend/APIArena.sln package --vulnerable --include-transitive` | 0 |
| `npm audit --omit=dev` | 0 |
| `npm audit` (dev dahil) | 5 high — hepsi `braces` (F3, gerekçeli kabul, yalnızca lint aracı) |
| Commit adayında kişisel veri/sabit sır taraması (`git grep`: gerçek e-posta, kullanıcı yolu, parola/token atamaları) | Bulgu yok (yalnızca `SET_IN_SECRET_STORE` yer tutucuları) |
| Playwright | Bu turda çalıştırılmadı (yalnızca renk/menü sırası ve koşu ekranına snippet eklendi) |
