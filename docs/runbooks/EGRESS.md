# Runbook: webhook egress kısıtı (production ön koşulu)

Uygulama katmanı kontrolleri (URL politikası, DNS pinleme, IP sınıfı filtresi) **tek başına yeterli sayılmaz**. Webhook modülü production'da yalnızca aşağıdaki ağ kısıtları kurulup doğrulandıktan sonra açılabilir; `ApiArena:Webhooks:EgressRestrictionAttested=true` olmadan `ProductionConfigGuard` host'u başlatmaz.

> Durum (2026-10-07): **Doğrulanmadı / Blocked.** Hosting ortamı yok. Aşağıdaki adımlar hazırlanmış yapılandırmadır, uygulanmış ve test edilmiş değildir.

## Hedef mimari
- Giden webhook isteklerini **yalnızca Worker** yapar. Control API ve Simulator'ın internete çıkışı gerekmez.
- Worker ayrı bir ağ segmentinde/namespace'te çalışır; çıkış kuralları:
  - İzinli: TCP 443 ve 8443 → `0.0.0.0/0` **hariç** aşağıdaki aralıklar; UDP/TCP 53 yalnızca belirlenmiş resolver'a.
  - Engelli: `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16` (metadata dahil), `0.0.0.0/8`, `224.0.0.0/4`, `240.0.0.0/4`, IPv6 `fc00::/7`, `fe80::/10`, `::1/128`, `::ffff:0:0/96`, `64:ff9b::/96`.
  - DB'ye erişim yalnızca DB alt ağına 1433 (veya özel endpoint) — webhook hedef URL'si üzerinden değil.
- Cloud metadata: IMDSv2 hop limit = 1 (AWS) / metadata erişimi kapalı ağ politikası.

## Örnek: Linux nftables (worker host'u)
```
table inet apiarena_egress {
  chain output {
    type filter hook output priority 0; policy drop;
    ct state established,related accept
    oifname "lo" accept
    ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 0.0.0.0/8, 224.0.0.0/4, 240.0.0.0/4 } drop
    ip6 daddr { fc00::/7, fe80::/10, ::1/128, ::ffff:0:0/96, 64:ff9b::/96 } drop
    ip daddr <DB_SUBNET> tcp dport 1433 accept
    ip daddr <RESOLVER_IP> udp dport 53 accept
    ip daddr <RESOLVER_IP> tcp dport 53 accept
    tcp dport { 443, 8443 } accept
  }
}
```
(`<DB_SUBNET>` DB'nin özel alt ağıdır; yukarıdaki genel "özel aralık drop" kuralından **önce** gelmelidir — sırayı ortamınıza göre ayarlayın.)

## Doğrulama (yayın öncesi zorunlu)
Worker container'ı içinden:
1. `curl -m 3 http://169.254.169.254/` → zaman aşımı/bağlantı reddi.
2. `curl -m 3 http://<DB_IP>:1433` ve `http://10.x.x.x` → reddedilir (DB alt ağı kuralı yalnızca uygulama sürücüsü içindir; ayrı test kullanıcısıyla doğrulayın).
3. `curl -m 5 https://example.com` → başarılı.
4. API Arena'da `https://<kontrol ettiğiniz alan adı>` → `127.0.0.1`'e çözümlenen bir kayıtla hedef oluşturup doğrulama denemesi → `blocked_address`.
Sonuçları `docs/TEST_RESULTS.md`'ye ekleyin; ancak ondan sonra `EgressRestrictionAttested=true` ve `Enabled=true` yapın.
