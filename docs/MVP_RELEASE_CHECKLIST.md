# MVP çıkış kapısı (Aşama 12)

Tarih: 2026-10-07. Durum değerleri: **Passed** (kanıtlı), **Blocked** (dış erişim/karar gerekli), **NotApplicable**.

| # | Kapı | Durum | Kanıt / gerekçe |
|---|---|---|---|
| 1 | 00–12 kabul satırları | Passed | `docs/ACCEPTANCE.md` (AUTH, OWN, RUN, SIM, REP-01…06, SEC, UX, OPS-01…03 Verified) |
| 2 | Kritik/yüksek istismar edilebilir güvenlik bulgusu yok | Passed | `docs/SECURITY_REVIEW.md` — F3 (dev-only `braces`) gerekçeli kabul |
| 3 | Gerçek demo akışı | Passed | `scripts/demo.ps1 -Scenario retry-503` canlı yığında: naive 1 istek (503), resilient 503→503→200; `Stage09_ExampleClientTests` 3 senaryo |
| 4 | Backend build + unit + integration (gerçek SQL Server) | Passed | `scripts/test.ps1` exit 0 (33 + 84 test) |
| 5 | Frontend lint + typecheck + test + build | Passed | `scripts/test.ps1` exit 0 (29 test) |
| 6 | Playwright ana akış + responsive + a11y | Passed | `scripts/e2e.ps1` 5/5 |
| 7 | Bağımlılık + secret taraması | Passed (yerel) | dotnet: 0 açık; npm prod: 0; gitleaks working tree: 0 (git geçmişi yok) |
| 8 | CI workflow | **Yazıldı, remote run doğrulanmadı** | `.github/workflows/ci.yml` actionlint 1.7.12 temiz; repo GitHub'a push edilmedi (kullanıcı yetkisi/remote yok) |
| 9 | README kurulumu tekrarlanabilir | Passed (Windows native) / **Blocked (Docker)** | Boş DB'ye `scripts/db-migrate.ps1` (2 migration), `scripts/test.ps1` temiz `npm ci` ile; Docker yolu bu makinede Docker olmadığı için çalıştırılamadı |
| 10 | Production deploy | NotApplicable (MVP) | Hosting/domain/secret kullanıcıdan; Aşama 17 |

**Sonuç:** Kullanılabilir çekirdek MVP yerelde hazır. Webhook, özel senaryo, paylaşım ve ticari özellikler bu kapının parçası değildir.

## Kullanıcıdan gerekenler (MVP için)
- GitHub repository oluşturma ve push izni → CI'ın gerçekten koşması.
- (İsteğe bağlı) macOS/Linux makinede `docker compose` yolunun denenmesi.
