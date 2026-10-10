# Durum

Son güncelleme: 2026-10-07

| Aşama | Ad | Bağımlılık | Durum | Not |
|---|---|---|---|---|
| 00 | Keşif, kurallar, plan | — | Done | Yalnızca plan/belge aşaması |
| 01 | Çalışan temel ve yerel kurulum | 00 | Done | Docker Compose yazıldı, bu makinede doğrulanamadı (Docker yok) |
| 02 | Hesap, oturum, hesap güvenliği | 01 | Done | Hesap silme 16'da |
| 03 | Projeler, veri modeli, erişim sınırları | 02 | Done | |
| 04 | Senaryo kataloğu, koşu yaşam döngüsü | 03 | Done | |
| 05 | Deterministik simülatör | 04 | Done | |
| 06 | Trafik kaydı, canlı ekran | 05 | Done | |
| 07 | Değerlendirme motoru | 06 | Done | |
| 08 | Rapor, karşılaştırma, export | 07 | Done | |
| 09 | Örnek istemci, demo | 05, 08 | Done | |
| 10 | Arayüzün tamamlanması | 08, 09 | Done | Görsel QA: Playwright ekran görüntüleri + axe |
| 11 | MVP güvenlik denetimi | 10 | Done | docs/SECURITY_REVIEW.md; F6 (DataProtection anahtar şifrelemesi) yayın kapısı |
| 12 | MVP CI, çıkış kapısı | 11 | Done | Workflow yazıldı, **remote run doğrulanmadı** (push/remote yok); Docker yolu doğrulanamadı |
| 13 | Webhook hedefleri, worker | 12 | Blocked (production) / yerel Done | Uygulama katmanı SSRF korumaları, outbox, HMAC doğrulama tamam ve testli; **production egress kısıtı uygulanmadı/doğrulanmadı** → production'da flag kapalı kalmalı |
| 14 | Webhook senaryoları, örnek alıcı | 13 | Done (yerel) | Production kullanımı 13'teki egress kapısına bağlı |
| 15 | Özel senaryo editörü | 12 (05, 07) | Done | Backend + editör UI (önizleme=simülatör, içe/dışa aktarma, sürümler, arşiv); Stage15 + E2E |
| 16 | Paylaşım, yaşam döngüsü, ops | 12, 13 | Done (yerel) | Paylaşım, retention işi, metrikler, hesap silme, restore drill testli; **izleme alıcısı/alarmlar bağlanmadı**, production yedek/anahtar restore Blocked |
| 17 | v1 yayınlama ve son kabul | 13–16 | Blocked (dış erişim) | Kod/test kapıları yerelde geçti; Docker/hosting/TLS/remote CI/GitHub reporting/egress/alert alıcısı erişimi yok — public yayın yapılmadı |
| 18 | Gelir modeli doğrulama (opsiyonel) | 17 | NotStarted | Kapsam dışı (kullanıcı kararı) |
| 19 | Ekipler/CI/abonelik (opsiyonel) | 17, 18 | NotStarted | Kapsam dışı (kullanıcı kararı) |

## Done kriterleri
- **Çekirdek MVP (00–12):** ACCEPTANCE'taki AUTH/OWN/RUN/SIM/REP-01…06/SEC/OPS-01…03,07/UX/DOC-01…04 `Verified`; kritik güvenlik bulgusu yok; gerçek demo akışı (yanlış/düzeltilmiş istemci) geçti.
- **Portföy v1 (13–17):** + WH-*, REP-07, SIM-11, SEC-06, OPS-04…09, DOC-05; webhook yerel demo çalışır; public açılışta egress doğrulanmadıysa webhook kapalı.
- **Ticari (18–19):** ayrı release gate; bu çalışmada uygulanmayacak.
