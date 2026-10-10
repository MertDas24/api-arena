import Link from "next/link";

const steps = [
  { title: "Senaryoyu seç", text: "503 sonrası yeniden deneme, 429 + Retry-After veya yavaş yanıt ve zaman aşımı." },
  { title: "Adresi ve anahtarı bağla", text: "Her koşuya ayrı sahte servis URL'si ve kısa ömürlü erişim anahtarı verilir." },
  { title: "Testi başlat ve gözle", text: "Uygulamanın gönderdiği istekler canlı listelenir; sunucunun gerçekte ne gördüğü kaydedilir." },
  { title: "Kanıta dayalı raporu oku", text: "Her kontrol Passed, Failed, Inconclusive veya NotApplicable; her sonucun kanıtı gösterilir." },
];

const limits = [
  "HTTP gözlemi, uygulamanın veritabanında ne olduğunu kanıtlamaz; rapor bunu ayrı belirtir.",
  "Kanıt yoksa sonuç Inconclusive olur ve başarı sayılmaz. Genel bir “%100 dayanıklı” puanı yoktur.",
  "Mockoon, WireMock ve Webhook.site gibi benzer araçlar vardır; API Arena'nın odağı adım adım öğrenme ve açıklamalı geri bildirimdir.",
];

export default function LandingPage() {
  return (
    <div className="flex flex-col gap-12">
      <section className="pt-6 sm:pt-12">
        <p className="mb-3 inline-block rounded-full border border-accent-500/50 px-3 py-1 text-xs text-accent-300">Junior geliştiriciler ve küçük ekipler için</p>
        <h1 className="max-w-3xl text-3xl font-bold leading-tight tracking-tight sm:text-5xl">
          Uygulaman bir API hata verdiğinde <span className="text-accent-300">gerçekten</span> ne yapıyor?
        </h1>
        <p className="mt-4 max-w-2xl text-fog-300">
          API Arena, uygulamanı kontrollü hata senaryolarına bağlar: iki kez 503 sonra 200, 429 ve Retry-After, 3 saniyelik gecikme. İsteklerini canlı izler ve neyi doğrulayabildiğini, neyi
          doğrulayamadığını açıkça söyleyen bir rapor üretir.
        </p>
        <div className="mt-6 flex flex-wrap gap-3">
          <Link href="/register" className="rounded-md bg-accent-500 px-5 py-3 font-semibold text-white hover:bg-accent-400">
            Ücretsiz hesap oluştur
          </Link>
          <Link href="/login" className="rounded-md border border-ink-500 px-5 py-3 font-semibold hover:bg-ink-800">
            Giriş yap
          </Link>
        </div>
      </section>

      <section aria-labelledby="how">
        <h2 id="how" className="mb-4 text-xl font-bold">
          Nasıl çalışır?
        </h2>
        <ol className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {steps.map((s, i) => (
            <li key={s.title} className="rounded-xl border border-ink-600 bg-ink-850 p-4">
              <span className="font-mono text-xs text-accent-300">Adım {i + 1}</span>
              <h3 className="mt-1 font-semibold">{s.title}</h3>
              <p className="mt-1 text-sm text-muted">{s.text}</p>
            </li>
          ))}
        </ol>
      </section>

      <section aria-labelledby="honest" className="rounded-xl border border-ink-600 bg-ink-900 p-5">
        <h2 id="honest" className="mb-3 text-lg font-bold">
          Neyi iddia etmiyoruz?
        </h2>
        <ul className="list-disc space-y-2 pl-5 text-sm text-fog-300">
          {limits.map((l) => (
            <li key={l}>{l}</li>
          ))}
        </ul>
      </section>
    </div>
  );
}
