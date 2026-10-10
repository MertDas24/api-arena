"use client";

import { HttpStatus, OutcomeText } from "./status";
import { ErrorState, Spinner } from "./ui";
import { formatDuration, formatTime } from "@/lib/format";
import type { ObservationDetail } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

/**
 * All recorded request data is rendered strictly as text nodes inside <pre> (SEC-04): React escapes it, and there is
 * no dangerouslySetInnerHTML/eval anywhere in the app. Headers were masked server-side before storage.
 */
export function ObservationDetailPanel({ runId, observationId }: { runId: string; observationId: string }) {
  const { data, error, loading, reload } = useApi<ObservationDetail>(`/api/runs/${runId}/observations/${observationId}`);
  if (loading) return <Spinner label={tr.common.loading} />;
  if (error || !data) return <ErrorState error={error} onRetry={reload} />;
  const s = data.summary;
  return (
    <div className="flex flex-col gap-3 text-sm" data-testid="observation-detail">
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
        <dt className="text-muted">#</dt>
        <dd className="font-mono">{s.sequence}</dd>
        <dt className="text-muted">İstek</dt>
        <dd className="break-all font-mono">
          {s.method} /{s.route}
        </dd>
        <dt className="text-muted">Idempotency-Key</dt>
        <dd className="break-all font-mono">{s.operationKey ?? "—"}</dd>
        <dt className="text-muted">Deneme</dt>
        <dd>{s.attempt || "—"}</dd>
        <dt className="text-muted">Alındı</dt>
        <dd>{formatTime(s.receivedAt)}</dd>
        <dt className="text-muted">Planlanan</dt>
        <dd>
          <HttpStatus code={s.plannedStatus} simulated={s.isSimulatedError} rejected={s.outcome === "Rejected"} />
          {s.plannedDelayMs > 0 && <span className="text-muted"> · {s.plannedDelayMs} ms gecikme</span>}
        </dd>
        <dt className="text-muted">Gerçekleşen</dt>
        <dd>
          <OutcomeText outcome={s.outcome} /> · {formatDuration(s.durationMs)}
          {s.rejectReason && <code className="ml-1 font-mono text-xs">({s.rejectReason})</code>}
        </dd>
      </dl>
      <div>
        <h3 className="mb-1 font-semibold">{tr.live.headers}</h3>
        <pre tabIndex={0} className="max-h-48 overflow-auto rounded border border-ink-600 bg-ink-950 p-2 font-mono text-xs whitespace-pre-wrap break-all">{JSON.stringify(data.headers, null, 2)}</pre>
      </div>
      <div>
        <h3 className="mb-1 font-semibold">
          {tr.live.body} <span className="font-normal text-muted">({data.bodyBytes} bayt)</span>
        </h3>
        {data.bodyCaptureEnabled ? (
          <pre tabIndex={0} className="max-h-48 overflow-auto rounded border border-ink-600 bg-ink-950 p-2 font-mono text-xs whitespace-pre-wrap break-all" data-testid="body-preview">
            {data.bodyPreview ?? "—"}
            {data.bodyTruncated && "\n… (kısaltıldı)"}
          </pre>
        ) : (
          <p className="text-muted">{tr.live.bodyOff}</p>
        )}
      </div>
      {data.responsePreview && (
        <div>
          <h3 className="mb-1 font-semibold">{tr.live.response}</h3>
          <pre tabIndex={0} className="max-h-40 overflow-auto rounded border border-ink-600 bg-ink-950 p-2 font-mono text-xs whitespace-pre-wrap break-all">{data.responsePreview}</pre>
        </div>
      )}
    </div>
  );
}
