"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useState } from "react";
import { HealthBadge } from "./health-badge";
import { useSession } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

const links = [
  { href: "/account", label: tr.nav.account },
  { href: "/dashboard", label: tr.nav.dashboard },
  { href: "/projects", label: tr.nav.projects },
  { href: "/scenarios", label: tr.nav.scenarios },
  { href: "/compare", label: tr.nav.compare },
];

export function AppHeader() {
  const { me, logout } = useSession();
  const pathname = usePathname();
  const router = useRouter();
  const [open, setOpen] = useState(false);

  return (
    <header className="border-b border-ink-600 bg-ink-900">
      <div aria-hidden className="brand-stripe" />
      <div className="mx-auto flex max-w-6xl items-center justify-between gap-3 px-4 py-3 sm:px-6">
        <Link href={me ? "/dashboard" : "/"} className="flex items-center gap-2 font-bold tracking-tight">
          <span aria-hidden className="relative grid h-8 w-8 place-items-center overflow-hidden rounded-md bg-accent-500 text-xs font-black text-white">
            <span className="absolute right-0 top-0 h-3 w-3 rounded-bl-md bg-sun" />
            <span className="absolute bottom-0 left-0 h-1.5 w-full bg-flame" />
            <span className="relative">AA</span>
          </span>
          <span>{tr.app.name}</span>
        </Link>

        <div className="flex items-center gap-2">
          <HealthBadge />
          {me && (
            <button
              type="button"
              className="rounded-md border border-ink-500 px-3 py-2 text-sm md:hidden"
              aria-expanded={open}
              aria-controls="main-nav"
              onClick={() => setOpen((v) => !v)}
            >
              {open ? tr.nav.closeMenu : tr.nav.openMenu}
            </button>
          )}
        </div>
      </div>

      <nav id="main-nav" aria-label={tr.nav.mainNav} className={`${me && open ? "block" : "hidden"} border-t border-ink-700 md:block md:border-0`}>
        <div className="mx-auto flex max-w-6xl flex-col gap-1 px-4 pb-3 sm:px-6 md:flex-row md:items-center md:pb-2">
          {me ? (
            <>
              {links.map((l) => {
                const active = pathname === l.href || pathname.startsWith(l.href + "/");
                return (
                  <Link
                    key={l.href}
                    href={l.href}
                    aria-current={active ? "page" : undefined}
                    onClick={() => setOpen(false)}
                    className={`rounded-md px-3 py-2 text-sm ${active ? "bg-ink-700 font-semibold text-fog-100" : "text-fog-300 hover:bg-ink-800"}`}
                  >
                    {l.label}
                  </Link>
                );
              })}
              <span className="hidden flex-1 md:block" />
              <span className="truncate px-3 py-2 text-xs text-muted" title={me.email}>
                {me.email}
              </span>
              <button
                type="button"
                className="rounded-md px-3 py-2 text-left text-sm text-fog-300 hover:bg-ink-800"
                onClick={async () => {
                  await logout();
                  router.push("/login");
                }}
              >
                {tr.nav.logout}
              </button>
            </>
          ) : (
            <div className="hidden gap-1 md:ml-auto md:flex">
              <Link href="/login" className="rounded-md px-3 py-2 text-sm text-fog-300 hover:bg-ink-800">
                {tr.nav.login}
              </Link>
              <Link href="/register" className="rounded-md bg-accent-500 px-3 py-2 text-sm font-semibold text-white hover:bg-accent-400">
                {tr.nav.register}
              </Link>
            </div>
          )}
        </div>
      </nav>
    </header>
  );
}
