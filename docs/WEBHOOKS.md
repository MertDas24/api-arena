# Webhook sözleşmesi (v1)

Webhook senaryolarında istekleri **API Arena gönderir**, kullanıcının alıcısı karşılar. Modül varsayılan **kapalıdır** (`ApiArena:Webhooks:Enabled=false`); kapalıyken ilgili endpoint'ler hiç map edilmez ve worker webhook işi çalıştırmaz.

## Teslim isteği
```
POST {target-url}
Content-Type: application/json; charset=utf-8
User-Agent: APIArena-Webhooks/1.0
X-Arena-Event-Id: evt_sim_<run>_1          # mantıksal olay — yinelenen teslimlerde AYNI
X-Arena-Delivery-Id: <guid>                # her deneme için FARKLI (retry/replay dahil)
X-Arena-Run-Id: <run guid>
X-Arena-Timestamp: <unix seconds>
X-Arena-Signature: v1=<hex HMAC-SHA256(secret, "{timestamp}.{deliveryId}.{rawBody}")>

{"id":"evt_sim_…","type":"payment.succeeded","runId":"…","aggregateId":"pay_sim_…","version":1,
 "data":{"paymentId":"pay_sim_…","status":"succeeded","amount":4200,"currency":"TRY"},"synthetic":true,"note":"…"}
```
Alıcı kuralları (örnek: `examples/webhook-receiver`):
1. İmzayı **ham gövde** üzerinden hesaplayıp **sabit zamanlı** karşılaştır (`CryptographicOperations.FixedTimeEquals`).
2. `|now − timestamp| ≤ 300 s` değilse reddet (replay penceresi).
3. `payload.runId == X-Arena-Run-Id` (koşu bağlama).
4. `X-Arena-Delivery-Id` daha önce görüldüyse işlemi tekrar uygulamadan 200 dön (replay / gönderici çökmesi sonrası yeniden gönderim).
5. `eventId` için benzersiz kısıt + iş güncellemesi **aynı transaction'da**; yinelenen olay → 200 no-op.
6. Aggregate sürümü: yalnızca `Version < event.version` ise uygula; eski olay → 200 no-op.

## Teslim semantiği
- **At-least-once.** Worker `SentAt`'i ağ çağrısından önce kaydeder; gönderim sonrası ama sonuç kaydından önce çökerse iş, lease süresi dolunca **aynı delivery id ile** tekrar gönderilir. Exactly-once gönderim iddia edilmez.
- Başarısız/zaman aşımı/5xx/429 → yeni deneme (yeni delivery id, aynı event id), en fazla 3 deneme, 2·n sn bekleme. Planlanan sıra (`plannedOrder`, `scheduledAt`) ile gerçekleşen (`sentAt`) ayrı tutulur.
- Koşu başına en fazla 10 teslim ve 2 istek/sn; koşu bitince/iptal edilince bekleyen teslimler iptal edilir.

## Hedef doğrulama (sahiplik kanıtı)
API Arena hedefe imzalı `{"type":"apiarena.verification","challenge":"<rastgele 64 hex>","targetId":"…"}` gönderir; alıcı
`{"challengeResponse":"<hex HMAC-SHA256(secret, "apiarena-verify." + challenge)>"}` döner. Sırrı yalnızca hedef sahibi bildiği için genel amaçlı "echo" servisleri doğrulanamaz. Doğrulama 30 gün geçerlidir; URL veya sır değişince düşer. Doğrulanmış olmak iç ağ adreslerine **izin vermez**.

## Read-only adapter (isteğe bağlı)
Koşu bitince worker, hedefle **aynı origin**'deki adapter yoluna imzalı `{"type":"apiarena.adapter.snapshot","runId","nonce","aggregateIds","eventIds"}` gönderir. Yanıt şeması katıdır (bilinmeyen alan reddedilir) ve `runId` + `nonce` geri dönmelidir:
```json
{"runId":"…","nonce":"…","aggregates":[{"aggregateId":"…","status":"captured","version":2,"amount":4200}],
 "events":[{"eventId":"…","processedCount":1}]}
```
Adapter yalnızca sentetik koşu verisini okumalı, hiçbir şey yazmamalı; production DB kimlik bilgisi istenmez. Sonuçlar raporda **"Adapter bildirdi" (AdapterReported)** olarak işaretlenir — kullanıcının sistemi tarafından üretildiği için bağımsız doğrulama değildir.

## SSRF korumaları (özet)
Tek giden istemci (`SafeHttpClient`): yalnızca HTTPS + 443/8443, IP literal/alternatif gösterim/yerel adlar yok, her yeni bağlantıda tüm A/AAAA kayıtları genel adres olmalı ve soket doğrulanan IP'ye bağlanır (DNS rebinding penceresi yok), TLS orijinal host adıyla doğrulanır, yönlendirme izlenmez, yanıt 64 KiB ve 10 sn ile sınırlı, hata mesajları yalnızca kod. Production'da ayrıca **ağ katmanı egress kısıtı** zorunludur: [runbooks/EGRESS.md](runbooks/EGRESS.md).
