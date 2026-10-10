import type { Metadata, Viewport } from "next";
import { headers } from "next/headers";
import "./globals.css";
import { AppHeader } from "@/components/app-header";
import { SessionProvider } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

export const metadata: Metadata = {
  title: { default: "API Arena", template: "%s · API Arena" },
  description: tr.app.tagline,
  referrer: "no-referrer",
};

export const viewport: Viewport = {
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#ffffff" },
    { media: "(prefers-color-scheme: dark)", color: "#0a1024" },
  ],
  width: "device-width",
  initialScale: 1,
};

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  // Reading the request headers opts every page into dynamic rendering, which the nonce-based CSP requires.
  await headers();
  return (
    <html lang="tr" className="h-full antialiased">
      <body className="flex min-h-full flex-col">
        <a href="#main" className="sr-only-focusable absolute left-2 top-2 z-50 rounded bg-accent-500 px-3 py-2 text-white">
          {tr.app.skipToContent}
        </a>
        <SessionProvider>
          <AppHeader />
          <main id="main" className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 sm:px-6 sm:py-8">
            {children}
          </main>
          <footer className="border-t border-ink-700 px-4 py-4 text-center text-xs text-muted">
            API Arena · Eğitim ve test amaçlı simülasyon. Gerçek ödeme veya müşteri verisi işlemez.
          </footer>
        </SessionProvider>
      </body>
    </html>
  );
}
