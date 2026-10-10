"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { RequireAuth } from "@/components/require-auth";
import { CheckBadge, RunStatusBadge } from "@/components/status";
import { Alert, Button, Card, EmptyState, ErrorState, PageHeader, Spinner } from "@/components/ui";
import { formatDateTime, formatDuration, shortId } from "@/lib/format";
import type { CompareResult, CompareSide, Dashboard } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function Side({ title, side }: { title: string; side: CompareSide }) {
  return (
    <Card>
      <h2 className="text-sm uppercase tracking-wide text-muted">{title}</h2>
      <p className="mt-1 font-semibold">
        <code className="font-mono">{side.scenarioSlug}</code> v{side.scenarioVersion} {side.label && <span className="text-muted">· {side.label}</span>}
      </p>
      <p className="text-xs text-muted">
        {formatDateTime(side.createdAt)} · <code className="font-mono">{side.evaluatorVersion}</code>
      </p>
      <div className="mt-2 flex flex-wrap items-center gap-2 text-sm">
        <RunStatusBadge status={side.runStatus} />
        <span>
          ✓ {side.counts.passed} · ✕ {side.counts.failed} · ? {side.counts.inconclusive} · – {side.counts.notApplicable}
        </span>
      </div>
      <p className="mt-1 text-xs text-muted">
        {side.requestCount} istek · süre {formatDuration(side.runDurationMs)}
      </p>
      <Link className="mt-2 inline-block text-sm text-accent-300 underline" href={`/runs/${side.runId}/report`}>
        {tr.live.openReport}
      </Link>
    </Card>
  );
}

function Picker({ left, right }: { left: string; right: string }) {
  const router = useRouter();
  const dashboard = useApi<Dashboard>("/api/dashboard");
  const [l, setL] = useState(left);
  const [r, setR] = useState(right);
  const runs = dashboard.data?.recentRuns.filter((x) => x.hasReport) ?? [];
  if (dashboard.loading) return <Spinner label={tr.common.loading} />;
  if (runs.length < 2) return <EmptyState title="Karşılaştırma için raporu olan en az iki koşu gerekli." />;
  const option = (x: (typeof runs)[number]) => (
    <option key={x.id} value={x.id}>
      {x.scenarioSlug} {x.label ? `· ${x.label}` : ""} · {formatDateTime(x.createdAt)} · {shortId(x.id)}
    </option>
  );
  return (
    <Card>
      <form
        className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-end"
        onSubmit={(e) => {
          e.preventDefault();
          router.push(`/compare?left=${l}&right=${r}`);
        }}
      >
        <div className="flex flex-col gap-1">
          <label htmlFor="left" className="text-sm">
            {tr.compare.left}
          </label>
          <select id="left" value={l} onChange={(e) => setL(e.target.value)} className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
            <option value="">—</option>
            {runs.map(option)}
          </select>
        </div>
        <div className="flex flex-col gap-1">
          <label htmlFor="right" className="text-sm">
            {tr.compare.right}
          </label>
          <select id="right" value={r} onChange={(e) => setR(e.target.value)} className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
            <option value="">—</option>
            {runs.map(option)}
          </select>
        </div>
        <Button type="submit" disabled={!l || !r || l === r}>
          {tr.compare.run}
        </Button>
      </form>
    </Card>
  );
}

function Comparison() {
  const params = useSearchParams();
  const left = params.get("left") ?? "";
  const right = params.get("right") ?? "";
  const ok = /^[0-9a-f-]{36}$/i.test(left) && /^[0-9a-f-]{36}$/i.test(right);
  const { data, error, loading, reload } = useApi<CompareResult>(ok ? `/api/runs/compare?left=${left}&right=${right}` : null);

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={tr.compare.title} />
      <Picker key={`${left}-${right}`} left={left} right={right} />
      {ok && loading && <Spinner label={tr.common.loading} />}
      {error ? <ErrorState error={error} onRetry={reload} /> : null}
      {data && (
        <>
          {data.warnings.map((w) => (
            <Alert key={w} tone="warning">
              {w}
            </Alert>
          ))}
          <div className="grid gap-3 md:grid-cols-2">
            <Side title={tr.compare.left} side={data.left} />
            <Side title={tr.compare.right} side={data.right} />
          </div>
          {data.comparable ? (
            <div tabIndex={0} className="relative overflow-x-auto rounded-lg border border-ink-600">
              <table className="w-full min-w-[560px] text-sm">
                <caption className="sr-only">Kontrol değişimleri</caption>
                <thead className="bg-ink-800 text-left text-xs uppercase text-muted">
                  <tr>
                    <th scope="col" className="p-2">Kontrol</th>
                    <th scope="col" className="p-2">{tr.compare.left}</th>
                    <th scope="col" className="p-2">{tr.compare.right}</th>
                    <th scope="col" className="p-2">Değişim</th>
                  </tr>
                </thead>
                <tbody>
                  {data.checks.map((c) => (
                    <tr key={c.id} className="border-t border-ink-700">
                      <td className="p-2">
                        {c.title}
                        <div className="font-mono text-xs text-muted">{c.id}</div>
                      </td>
                      <td className="p-2">{c.left ? <CheckBadge status={c.left} /> : "—"}</td>
                      <td className="p-2">{c.right ? <CheckBadge status={c.right} /> : "—"}</td>
                      <td className="p-2 font-semibold">
                        <span aria-hidden>{c.change === "improved" ? "↑ " : c.change === "regressed" ? "↓ " : ""}</span>
                        {tr.compare.change[c.change] ?? c.change}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <Alert tone="info">{tr.compare.notComparable}</Alert>
          )}
        </>
      )}
    </div>
  );
}

export default function ComparePage() {
  return (
    <RequireAuth>
      <Suspense>
        <Comparison />
      </Suspense>
    </RequireAuth>
  );
}
