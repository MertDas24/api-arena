# Mert için kısa mimari notu

## Ana yol
**UI → Control API → Application → Domain/Infrastructure → SQL Server.** Next.js ekranları same-origin `/api` çağırır. `Api/Endpoints` HTTP'yi use-case servisine çevirir. `Application/Runs/RunService` kullanıcı sahipliği ve koşu geçişini yönetir. `Domain/Scenarios/ScenarioEngine` saf response kararını verir. `Infrastructure/Persistence/RunAtomics` SQL'de sayacı atomik artırır. Örnek: kullanıcı bir proje için retry koşusu başlatır; aynı kullanıcıya ait değilse 404.

## Ayrı simulator ve worker
Control API hesap/proje/rapor yönetir. Simulator ayrı hostta sadece katalog/snapshot route'u üzerinden sahte servis davranışını yürütür. Örnek: `POST /sim/runs/{id}/payment/charge` aynı işlem için ilk iki denemede 503, üçüncüde 200 döndürür. Worker SQL outbox job'larını işler ve koşu expiry tarar; dış webhook istekleri yalnız Worker'dan çıkar. Hostlar state'i SQL'de paylaşır, process içi sayaç yok.

## Cookie ve run-key
Cookie kişinin oturumudur: Identity server-side ticket, CSRF ve origin kontrolüyle control API'yi açar. `__Host-aa_session` production'da Secure/HttpOnly/Strict ve host-only'dir. Run-key belirli koşunun simülatör erişimidir: 256-bit random, DB'de hash, Bearer header, TTL/revoke. Örnek: kontrol cookie'siyle simulator'a gitmek 401; koşu bitince run-key de 401. localhost portları cookie ayırmaz; production farklı hostname + proxy Cookie kaldırma gerekir.

## Senaryo sürümü ve kanıt
Koşu immutable config/evaluator/seed snapshot alır. Örnek: özel template v2 eklemek v1 koşusunun raporunu değiştirmez. Gözlem sonucunda `Sent`, `ClientDisconnected`, `Unknown` ayrımı vardır; 503 beklenen simülasyondur, platform 500 başka bir sorundur. Evaluator expected/observed ve event sequence verir. Timeout client error UI'sı HTTP'den görülemez → Inconclusive. 200, DB'nin tek yazıldığını kanıtlamaz.

## SSRF
Hedef URL güvenilmez. `Domain/Outbound/NetworkPolicy` şema/IP/port/host sınıflarını, `Infrastructure/Outbound/SafeHttpClient` tüm DNS adreslerini denetler ve doğrulanan IP'ye bağlanır; TLS original host ile doğrulanır. Redirect kapalıdır. Örnek: public host DNS'te private IP'ye dönerse çağrı engellenir. Ayrıca gerçek ağ egress kısıtı gerekir: mevcut production profili bu kanıt olmadığı için webhook'u zorla kapatır.

## Outbox ve idempotency
API DB transaction'ında teslim planı/job yazar; Worker lease ile claim eder. Gönderim sırasında DB lock tutulmaz. Çökme sonrası lease expiry job'u yeniden claim edilebilir yapar. HTTP gönderimi sonrası DB sonucu yazılmadan crash olursa yeniden teslim mümkündür → at-least-once, exactly-once değil.

`examples/webhook-receiver/ReceiverStore` event ID unique + aynı transaction içinde business update/dedup kullanır. İki paralel aynı event → tek update. Aggregate version 2 önce, 1 sonra gelirse stale v1 uygulanmaz. Adapter read-only synthetic run sonucu döndürür; raporda AdapterReported olarak ayrıdır. Kendi alıcımızın SQL testleri DB sonucu için daha güçlü kanıttır.

## Operasyon ve henüz olmayanlar
Üç migration var; production normal app startup'ta migration yapmaz, ayrı `--migrate` job'udur. Yeni production Identity key ring PFX ile şifrelenir; DB backup yanında private cert backup gerekir. Yerel fresh migration/checksum restore ve bounded load ölçüldü; Docker/public TLS/hosting smoke yapılmadı.

Özel senaryo editörü (önizleme, içe/dışa aktarma, sürümler), salt okunur paylaşım linkleri, retention işi, düşük kardinaliteli metrikler ve hesap silme uygulandı ve testli. Metrik/alarm alıcısı bağlı değil; GitHub CI yeşil. Üyelik, CI service key, abonelik ve gerçek tahsilat kapsam dışı. Bunlar canlı özellik gibi anlatılmaz.
