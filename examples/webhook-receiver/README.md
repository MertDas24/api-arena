# API Arena — örnek ödeme webhook alıcısı

`duplicate-webhook-v1` ve `out-of-order-webhook-v1` senaryoları için **doğru** bir alıcı örneği (ve karşılaştırma için `naive` modu). Sentetik olaylar işler; gerçek ödeme yoktur.

| Mod | Davranış |
|---|---|
| `correct` (varsayılan) | İmza + zaman penceresi + koşu bağlama + delivery-id replay koruması; `ProcessedEvents.EventId` PRIMARY KEY ile iş güncellemesi **aynı transaction'da**; `Version < @v` sürüm koruması |
| `naive` | Tekilleştirme ve sürüm koruması yok (son yazan kazanır, tutar iki kez eklenir) — "önce" demosu |

Yinelenen veya eski olaylar **200 + no-op** ile onaylanır (gönderici gereksiz yere yeniden denemesin). Ayrıntılı sözleşme: [docs/WEBHOOKS.md](../../docs/WEBHOOKS.md).

## Yerel demo (Windows, geliştirme profili)
1. Webhook modülünü yerelde aç (yalnızca Development; production'da egress kısıtı şart):
   ```powershell
   ./scripts/dev-secrets.ps1 -Webhooks      # .env'e rastgele AES anahtarı + webhook bayrakları yazar (değer gösterilmez)
   ./scripts/dev.ps1
   ```
2. Arayüzde proje → **Webhook hedefleri** → URL `http://localhost:5160/webhooks/apiarena`, adapter yolu `/webhooks/apiarena/adapter` → **bir kez gösterilen** sırrı kopyala.
3. Alıcıyı başlat:
   ```powershell
   $env:RECEIVER_SIGNING_SECRET = [System.Net.NetworkCredential]::new("", (Read-Host -AsSecureString "Signing secret")).Password   # PowerShell 5.1 + 7, ekranda görünmez
   dotnet run --project examples/webhook-receiver --urls http://localhost:5160          # correct
   # $env:RECEIVER_MODE = "naive"  → hatalı davranış demosu
   ```
4. Hedefi **Doğrula** → durum "Doğrulandı" olur. **Yeni koşu** → `Yinelenen webhook` → hedefi seç → başlat → teslimleri izle → **Koşuyu bitir** → rapor.

Beklenen: `correct` modunda `processed-exactly-once` ve `final-state-correct` **Passed (Adapter bildirdi)**; `naive` modunda **Failed**. Her iki modda da teslimler 200 alır — **HTTP onayı iş doğruluğu değildir**.

Veritabanı: `RECEIVER_SQL` (varsayılan yerel `ApiArenaReceiverDemo`, Windows auth). Tablolar ilk açılışta oluşturulur. Bu otomatik akış `backend/tests/APIArena.IntegrationTests/Stage13_14_WebhookTests.cs` içinde gerçek alıcı koduyla test edilir.
