# API Arena — .NET örnek istemci

Simülatöre bağlanan küçük bir konsol uygulaması. Her senaryo için iki mod vardır:

| Mod | Davranış |
|---|---|
| `naive` | Bilerek yapılan yaygın hatalar: Idempotency-Key yok, yeniden deneme yok / beklemesiz yeniden deneme, zaman aşımı yok |
| `resilient` | Düzeltilmiş davranış: sabit Idempotency-Key, sınırlı üstel bekleme + jitter, `Retry-After`'a uyma, 1 sn zaman aşımı |

Gerçek ödeme, müşteri verisi veya dış servis yoktur; tüm yanıtlar simülatörün sentetik yanıtlarıdır.

## Çalıştırma

1. Arayüzde bir koşu oluşturun, anahtar alın ve koşuyu başlatın (veya `scripts/demo.ps1` kullanın).
2. Canlı koşu ekranındaki **SIM_BASE_URL** değerini Kopyala düğmesiyle alıp ortam değişkenine yazın (`<runId>` yer tutucusunu olduğu gibi bırakmayın).
3. İstemciyi çalıştırın; `Run key` sorulduğunda anahtarı yapıştırın. Yazarken ekranda görünmez ve komut geçmişine girmez (Windows PowerShell 5.1 dahil her kabukta). İsterseniz `RUN_KEY` ortam değişkeniyle de verebilirsiniz; komut satırı argümanı veya kaynak koda yazmayın.

```powershell
# PowerShell (Windows)
Remove-Item Env:RUN_KEY -ErrorAction SilentlyContinue
$env:SIM_BASE_URL = "http://localhost:5151/sim/runs/<runId>"
dotnet run --project examples/dotnet-client -- retry-503 naive
```

```bash
# bash / zsh
unset RUN_KEY
export SIM_BASE_URL="http://localhost:5151/sim/runs/<runId>"
dotnet run --project examples/dotnet-client -- rate-limit resilient
```

Adres geçersizse veya anahtar `aa_rk_` ile başlamıyorsa program açıklayıcı bir mesajla çıkar (kod 2); simülatöre bağlanamazsa kod 3.

Senaryolar: `retry-503`, `rate-limit`, `timeout`. Her mod için **ayrı bir koşu** kullanın ki raporlar karşılaştırılabilsin.

## Örnek iz (retry-503, resilient)

```
POST /payment/charge (deneme 1, key order-3f2a91c…) → 503
  281 ms bekleniyor…
POST /payment/charge (deneme 2, key order-3f2a91c…) → 503
  537 ms bekleniyor…
POST /payment/charge (deneme 3, key order-3f2a91c…) → 200
Sonuç: Ödeme 3. denemede başarılı. (3 istek)
```

## Beklenen rapor farkı

| Senaryo | naive | resilient |
|---|---|---|
| retry-503 | `idempotency-key-present`, `retried-after-503`, `reached-success` → **Failed** | 6 kontrolün hepsi **Passed** |
| rate-limit | `respected-retry-after`, `no-hammering` → **Failed** | 3 kontrolün hepsi **Passed** |
| timeout | `client-timeout-observed` → **Failed** (3 sn beklendi) | `client-timeout-observed`, `timeout-within-budget` → **Passed**; `client-error-handling` → **Inconclusive** (HTTP'den gözlenemez) |

Bu farklar `backend/tests/APIArena.IntegrationTests/Stage09_ExampleClientTests.cs` içinde gerçek simülatör ve değerlendiriciyle otomatik doğrulanır.

## Kendi projene uygulamak (isteğe bağlı)

GameVault, GetKalori gibi mevcut projelerinde dış API çağrısı yapan bir sınıf varsa:

1. O sınıfın temel adresini yapılandırmadan okunur hale getir (ör. `PaymentApi:BaseUrl`).
2. Test için bu değeri koşunun simülatör adresine (`SIM_BASE_URL`) ayarla, `Authorization: Bearer` başlığını ortam değişkeninden ekle.
3. Uygulamanı normal kullan; API Arena canlı ekranında istekleri izle, koşuyu bitirip raporu oku.
4. `ArenaClient.cs` içindeki `resilient` desenlerini (Idempotency-Key, backoff, Retry-After, timeout) kendi koduna uyarla ve yeni koşuyla karşılaştır.

Bu adımlar yalnızca öneridir; API Arena başka projelerini değiştirmez. Simülatör adresini production yapılandırmasına koyma.
