"use client";

import Link from "next/link";
import { NextStepPanel, nextStepFor } from "@/components/next-step";
import { RequireAuth } from "@/components/require-auth";
import { RunStatusBadge } from "@/components/status";
import { Alert, Card, EmptyState, ErrorState, PageHeader, Spinner } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import { useSession } from "@/lib/session";
import type { Dashboard } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function Stat({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Card className="flex flex-col gap-1">
      <span className="text-xs uppercase tracking-wide text-muted">{label}</span>
      <span className="text-2xl font-bold">{value}</span>
      {hint && <span className="text-xs text-muted">{hint}</span>}
    </Card>
  );
}

function DashboardView() {
  const { me } = useSession();
  const { data, error, loading, reload } = useApi<Dashboard>("/api/dashboard");
  if (loading) return <Spinner label={tr.common.loading} />;
  if (error || !data || !me) return <ErrorState error={error} onRetry={reload} />;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title={tr.dashboard.title} description={me.displayName ? `Merhaba ${me.displayName}` : me.email} />
      {!me.emailConfirmed && me.runsRequireVerifiedEmail && <Alert tone="warning">{tr.auth.unverifiedBanner}</Alert>}
      <NextStepPanel step={nextStepFor(me, data)} />

      <div className="grid gap-3 sm:grid-cols-3">
        <Stat label={tr.dashboard.projects} value={String(data.projectCount)} />
        <Stat label={tr.dashboard.activeRuns} value={`${data.activeRuns} / ${data.activeRunLimit}`} />
        <Stat label={tr.dashboard.dailyRuns} value={`${data.runsStartedLast24h} / ${data.dailyRunLimit}`} />
      </div>
      <p className="text-xs text-muted">{tr.dashboard.limitsNote(data.requestsPerRun, data.runTtlMinutes)}</p>

      <section aria-labelledby="recent">
        <h2 id="recent" className="mb-3 text-lg font-bold">
          {tr.dashboard.recent}
        </h2>
        {data.recentRuns.length === 0 ? (
          <EmptyState title={tr.dashboard.noRuns} />
        ) : (
          <ul className="flex flex-col gap-2">
            {data.recentRuns.map((r) => (
              <li key={r.id}>
                <Link
                  href={r.hasReport ? `/runs/${r.id}/report` : `/runs/${r.id}`}
                  className="flex flex-col gap-2 rounded-lg border border-ink-600 bg-ink-850 p-3 hover:border-accent-500 sm:flex-row sm:items-center sm:justify-between"
                >
                  <span className="min-w-0">
                    <span className="block truncate font-medium">
                      <code className="font-mono text-sm">{r.scenarioSlug}</code>
                      {r.label && <span className="text-muted"> · {r.label}</span>}
                    </span>
                    <span className="block truncate text-xs text-muted">
                      {r.projectName} · {formatDateTime(r.createdAt)}
                    </span>
                  </span>
                  <span className="flex flex-wrap items-center gap-2 text-xs">
                    {r.hasReport && (
                      <span className="text-muted">
                        {r.passed} passed · {r.failed} failed · {r.inconclusive} inconclusive
                      </span>
                    )}
                    <RunStatusBadge status={r.status} />
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

export default function DashboardPage() {
  return (
    <RequireAuth>
      <DashboardView />
    </RequireAuth>
  );
}
