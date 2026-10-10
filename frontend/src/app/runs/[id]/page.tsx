"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { ConnectionSnippets } from "@/components/connection-snippets";
import { ObservationDetailPanel } from "@/components/observation-detail";
import { RequireAuth } from "@/components/require-auth";
import { SecretOnce } from "@/components/secret-once";
import { HttpStatus, OutcomeText, RunStatusBadge } from "@/components/status";
import { Alert, Button, Card, CodeBlock, EmptyState, ErrorState, PageHeader, Spinner } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { filterTraffic, type TrafficFilter } from "@/lib/feed";
import { formatDuration, formatRemaining, formatTime } from "@/lib/format";
import type { CreatedRunKey, Run } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { useLiveFeed } from "@/lib/use-live-feed";
import { tr } from "@/lib/i18n/tr";

function Countdown({ expiresAt, serverTime }: { expiresAt: string; serverTime: string }) {
  const [now, setNow] = useState(() => Date.now());
  const [offset] = useState(() => new Date(serverTime).getTime() - Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);
  const remaining = (new Date(expiresAt).getTime() - (now + offset)) / 1000;
  return (
    <span className="font-mono" aria-label={`${tr.live.expiresIn}: ${formatRemaining(remaining)}`}>
      {formatRemaining(remaining)}
    </span>
  );
}

function KeyPanel({ run, onChanged }: { run: Run; onChanged: () => void }) {
  const [created, setCreated] = useState<CreatedRunKey | null>(null);
  const [error, setError] = useState<string | null>(null);
  const active = run.keys.find((k) => k.active);
  const ended = !["Created", "Running"].includes(run.status);
  return (
    <div className="flex flex-col gap-2 text-sm">
      <p>
        {tr.live.keyPrefix}: {active ? <code className="font-mono">{active.prefix}…</code> : <span className="text-muted">{tr.live.noKey}</span>}
      </p>
      {created && <SecretOnce value={created.key} label="Run key" />}
      {error && <Alert tone="error">{error}</Alert>}
      {!ended && (
        <ConfirmDialog
          title={tr.live.rotate}
          confirmLabel={tr.wizard.generateKey}
          onConfirm={async () => {
            try {
              setError(null);
              setCreated(await api<CreatedRunKey>(`/api/runs/${run.id}/access-keys`, { method: "POST" }));
              onChanged();
            } catch (e) {
              setError(e instanceof ApiError ? e.message : tr.common.errorTitle);
            }
          }}
          trigger={(open) => (
            <div>
              <Button variant="secondary" onClick={open}>
                {tr.live.rotate}
              </Button>
            </div>
          )}
        >
          Mevcut anahtarla gelen istekler hemen reddedilir (401). Yeni anahtar yalnızca bir kez gösterilir.
        </ConfirmDialog>
      )}
    </div>
  );
}

function Live() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const run = useApi<Run>(`/api/runs/${id}`);
  const [paused, setPaused] = useState(false);
  const [filter, setFilter] = useState<TrafficFilter>("all");
  const [selected, setSelected] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const { state, meta, connectionLost } = useLiveFeed(id, paused);

  const status = meta?.runStatus ?? run.data?.status;
  const ended = status !== undefined && !["Created", "Running"].includes(status);
  const reloadRun = run.reload;
  useEffect(() => {
    if (ended && run.data && ["Created", "Running"].includes(run.data.status)) reloadRun();
  }, [ended, run.data, reloadRun]);
  const webhookRun = run.data?.webhookTargetId ? run.data.id : null;
  useEffect(() => {
    if (webhookRun) router.replace(`/runs/${webhookRun}/webhook`);
  }, [webhookRun, router]);

  if (run.loading) return <Spinner label={tr.common.loading} />;
  if (run.error || !run.data) return <ErrorState error={run.error} onRetry={run.reload} />;
  const r = run.data;
  const rows = filterTraffic(state.observations, filter);
  const quota = (meta?.inboundCount ?? r.inboundCount) >= r.inboundLimit;

  async function act(path: "finish" | "cancel") {
    setActionError(null);
    try {
      await api<Run>(`/api/runs/${r.id}/${path}`, { method: "POST" });
      router.push(`/runs/${r.id}/report`);
    } catch (e) {
      setActionError(e instanceof ApiError ? e.message : tr.common.errorTitle);
      run.reload();
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={r.scenarioTitle}
        description={
          <>
            <Link className="underline" href={`/projects/${r.projectId}`}>
              {r.projectName}
            </Link>{" "}
            · <code className="font-mono">{r.scenarioSlug}</code> v{r.scenarioVersion}
            {r.label && ` · ${r.label}`}
          </>
        }
        actions={
          ended ? (
            <Link href={`/runs/${r.id}/report`} className="inline-flex min-h-10 items-center rounded-md bg-accent-500 px-4 py-2 text-sm font-semibold text-white">
              {tr.live.openReport} →
            </Link>
          ) : (
            <>
              {status === "Created" && (
                <Link href={`/projects/${r.projectId}/runs/new?run=${r.id}&step=2`} className="inline-flex min-h-10 items-center rounded-md bg-accent-500 px-4 py-2 text-sm font-semibold text-white">
                  {tr.wizard.step3} →
                </Link>
              )}
              {status === "Running" && (
                <ConfirmDialog title={tr.live.finish} confirmLabel={tr.live.finish} onConfirm={() => act("finish")} trigger={(open) => <Button onClick={open}>{tr.live.finish}</Button>}>
                  {tr.live.finishConfirm}
                </ConfirmDialog>
              )}
              <ConfirmDialog title={tr.live.cancel} confirmLabel={tr.live.cancel} danger onConfirm={() => act("cancel")} trigger={(open) => <Button variant="secondary" onClick={open}>{tr.live.cancel}</Button>}>
                {tr.live.cancelConfirm}
              </ConfirmDialog>
            </>
          )
        }
      />

      {actionError && <Alert tone="error">{actionError}</Alert>}
      {connectionLost && <Alert tone="warning">{tr.live.connectionLost}</Alert>}
      {quota && !ended && <Alert tone="warning">{tr.live.quotaReached}</Alert>}
      {ended && <Alert tone="info">{tr.live.ended}</Alert>}

      <div className="grid gap-3 sm:grid-cols-3">
        <Card className="flex items-center justify-between gap-2">
          <span className="text-sm text-muted">Durum</span>
          {status && <RunStatusBadge status={status} />}
        </Card>
        <Card className="flex items-center justify-between gap-2">
          <span className="text-sm text-muted">{tr.live.expiresIn}</span>
          {status === "Running" && meta?.expiresAt ? <Countdown expiresAt={meta.expiresAt} serverTime={meta.serverTime} /> : <span>—</span>}
        </Card>
        <Card className="flex items-center justify-between gap-2">
          <span className="text-sm text-muted">{tr.live.requests}</span>
          <span className="font-mono">
            {meta?.inboundCount ?? r.inboundCount} / {r.inboundLimit}
          </span>
        </Card>
      </div>

      {!ended && (
        <Card>
          <h2 className="mb-3 font-semibold">{tr.live.connection}</h2>
          <div className="mb-3 grid gap-3 lg:grid-cols-2">
            <CodeBlock label={tr.live.baseUrl} code={r.simulatorBaseUrl} />
            {r.simulatorUrl && <CodeBlock label={`${tr.live.fullUrl} (${r.method})`} code={r.simulatorUrl} />}
          </div>
          <KeyPanel run={r} onChanged={run.reload} />
          <div className="mt-4">
            <ConnectionSnippets baseUrl={r.simulatorBaseUrl} scenario={r.scenarioSlug} />
          </div>
        </Card>
      )}

      <section aria-labelledby="traffic" className="grid gap-4 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div className="min-w-0">
          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
            <h2 id="traffic" className="font-semibold">
              {tr.live.traffic}
            </h2>
            <div className="flex flex-wrap items-center gap-2">
              <label className="sr-only" htmlFor="traffic-filter">
                Filtre
              </label>
              <select id="traffic-filter" value={filter} onChange={(e) => setFilter(e.target.value as TrafficFilter)} className="min-h-9 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
                <option value="all">{tr.live.filterAll}</option>
                <option value="simError">{tr.live.filterSimErrors}</option>
                <option value="ok">{tr.live.filterOk}</option>
                <option value="rejected">{tr.live.filterRejected}</option>
              </select>
              {!ended && (
                <Button variant="secondary" className="min-h-9 py-1" aria-pressed={!paused} onClick={() => setPaused((p) => !p)} title={tr.live.pauseHint}>
                  {paused ? `▶ ${tr.live.paused}` : `⏸ ${tr.live.follow}`}
                </Button>
              )}
            </div>
          </div>
          {!ended && <p className="mb-2 text-xs text-muted">{tr.live.pauseHint}</p>}
          {state.observations.length === 0 ? (
            <EmptyState title={status === "Created" ? tr.live.notStarted : tr.live.waiting} />
          ) : (
            <div tabIndex={0} className="relative overflow-x-auto rounded-lg border border-ink-600" aria-live={paused ? "off" : "polite"}>
              <table className="w-full min-w-[560px] text-sm">
                <caption className="sr-only">{tr.live.traffic}</caption>
                <thead className="bg-ink-800 text-left text-xs uppercase text-muted">
                  <tr>
                    <th scope="col" className="p-2">#</th>
                    <th scope="col" className="p-2">Zaman</th>
                    <th scope="col" className="p-2">İstek</th>
                    <th scope="col" className="p-2">Deneme</th>
                    <th scope="col" className="p-2">Yanıt</th>
                    <th scope="col" className="p-2">Sonuç</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((o) => (
                    <tr key={o.id} className={`border-t border-ink-700 ${selected === o.id ? "bg-accent-500/15" : ""}`}>
                      <td className="p-2 font-mono">
                        <button type="button" className="underline decoration-dotted" onClick={() => setSelected(o.id)} aria-label={`İstek #${o.sequence} ayrıntısı`}>
                          {o.sequence}
                        </button>
                      </td>
                      <td className="p-2 font-mono text-xs">{formatTime(o.receivedAt)}</td>
                      <td className="max-w-[16rem] truncate p-2 font-mono text-xs" title={`${o.method} /${o.route}`}>
                        {o.method} /{o.route}
                      </td>
                      <td className="p-2">{o.attempt || "—"}</td>
                      <td className="p-2">
                        <HttpStatus code={o.plannedStatus} simulated={o.isSimulatedError} rejected={o.outcome === "Rejected"} />
                      </td>
                      <td className="p-2 text-xs">
                        <OutcomeText outcome={o.outcome} /> {o.durationMs !== null && <span className="text-muted">· {formatDuration(o.durationMs)}</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
        <Card className="min-w-0">
          <h2 className="mb-2 font-semibold">{tr.live.detail}</h2>
          {selected ? <ObservationDetailPanel key={selected} runId={r.id} observationId={selected} /> : <p className="text-sm text-muted">{tr.live.selectRow}</p>}
        </Card>
      </section>
    </div>
  );
}

export default function LiveRunPage() {
  return (
    <RequireAuth>
      <Live />
    </RequireAuth>
  );
}
