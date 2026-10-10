"use client";

import Link from "next/link";
import { RequireAuth } from "@/components/require-auth";
import { ErrorState, PageHeader, Spinner } from "@/components/ui";
import type { Scenario } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function Catalog() {
  const { data, error, loading, reload } = useApi<Scenario[]>("/api/scenarios");
  if (loading) return <Spinner label={tr.common.loading} />;
  if (error || !data) return <ErrorState error={error} onRetry={reload} />;

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={tr.scenarios.title}
        actions={
          <Link href="/scenarios/custom/new" className="inline-flex min-h-10 items-center rounded-md border border-ink-500 px-4 py-2 text-sm font-semibold hover:bg-ink-800">
            + {tr.scenarios.custom}
          </Link>
        }
      />
      <ul className="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
        {data.map((s) => (
          <li key={s.id}>
            <Link href={s.kind === "Custom" ? `/scenarios/custom/${s.id}` : `/scenarios/${s.slug}`} className="flex h-full flex-col gap-2 rounded-xl border border-ink-600 bg-ink-850 p-4 hover:border-accent-500">
              <span className="flex items-center justify-between gap-2">
                <code className="font-mono text-xs text-accent-300">{s.kind === "Custom" ? s.slug : s.slug}</code>
                <span className="text-xs text-muted">
                  v{s.latest.version} {s.kind === "Custom" ? `· ${tr.scenarios.custom}` : ""}
                </span>
              </span>
              <span className="font-semibold">{s.title}</span>
              <span className="text-sm text-muted">{s.summary}</span>
            </Link>
          </li>
        ))}
      </ul>
    </div>
  );
}

export default function ScenariosPage() {
  return (
    <RequireAuth>
      <Catalog />
    </RequireAuth>
  );
}
