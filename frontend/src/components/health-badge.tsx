"use client";

import { useEffect, useState } from "react";
import { tr } from "@/lib/i18n/tr";

type State = "checking" | "healthy" | "unhealthy" | "unreachable";

/** Shows the control API's real readiness (GET /api/health → DB check), not a hard-coded status. */
export function HealthBadge() {
  const [state, setState] = useState<State>("checking");
  const [version, setVersion] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const check = async () => {
      try {
        const res = await fetch("/api/health", { cache: "no-store" });
        const type = res.headers.get("content-type") ?? "";
        if (!type.includes("json")) {
          if (!cancelled) setState("unreachable");
          return;
        }
        const json = (await res.json()) as { status: string; version?: string };
        if (!cancelled) {
          setState(res.ok && json.status === "Healthy" ? "healthy" : "unhealthy");
          setVersion(json.version ?? null);
        }
      } catch {
        if (!cancelled) setState("unreachable");
      }
    };
    void check();
    const timer = setInterval(check, 30000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);

  const view = {
    checking: { dot: "bg-unknown", text: tr.health.checking, icon: "…" },
    healthy: { dot: "bg-ok", text: tr.health.healthy, icon: "✓" },
    unhealthy: { dot: "bg-warn", text: tr.health.unhealthy, icon: "!" },
    unreachable: { dot: "bg-bad", text: tr.health.unreachable, icon: "✕" },
  }[state];

  return (
    <span
      role="status"
      aria-label={`${tr.health.label}: ${view.text}`}
      title={version ? `${view.text} · v${version}` : view.text}
      data-testid="health-badge"
      data-state={state}
      className="inline-flex items-center gap-2 rounded-full border border-ink-600 px-2.5 py-1 text-xs text-fog-300"
    >
      <span aria-hidden className={`h-2 w-2 rounded-full ${view.dot}`} />
      <span aria-hidden>{view.icon}</span>
      <span className="hidden sm:inline">{view.text}</span>
    </span>
  );
}
