import { CheckBadge, EvidenceTag, HttpStatus, OutcomeText, RunStatusBadge } from "./status";
import { Alert, Card } from "./ui";
import { formatDateTime, formatDuration } from "@/lib/format";
import type { Report } from "@/lib/types";
import { tr } from "@/lib/i18n/tr";

function Tile({ label, value, icon, tone }: { label: string; value: number; icon: string; tone: string }) {
  return (
    <div className={`rounded-lg border-2 bg-ink-900 p-3 ${tone}`}>
      <div className="text-xs font-semibold">
        <span aria-hidden>{icon} </span>
        {label}
      </div>
      <div className="text-2xl font-bold">{value}</div>
    </div>
  );
}

/**
 * Shared report body (authenticated report page and public share). Everything is plain text; status is always
 * icon + word. There is deliberately no overall score — counts are shown side by side (C7).
 */
export function ReportView({ report, publicView }: { report: Report; publicView?: boolean }) {
  const c = report.counts;
  const applicable = c.passed + c.failed + c.inconclusive;
  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-center gap-2 text-sm text-muted">
        <RunStatusBadge status={report.runStatus} />
        <code className="font-mono">
          {report.scenarioSlug} v{report.scenarioVersion}
        </code>
        <span>·</span>
        <code className="font-mono">{report.evaluatorVersion}</code>
        <span>·</span>
        <span>
          {tr.report.generatedAt}: {formatDateTime(report.generatedAt)}
        </span>
      </div>

      {report.incompleteReasonText && (
        <Alert tone="warning" title={`${tr.report.incomplete}: ${report.incompleteReasonText}`}>
          Eksik kanıta dayanan sonuçlar Inconclusive olarak işaretlendi; hiçbir kontrol bu nedenle Passed sayılmadı.
        </Alert>
      )}

      <section aria-labelledby="counts">
        <h2 id="counts" className="mb-2 text-lg font-bold">
          {tr.report.counts}
        </h2>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
          <Tile label="Passed" value={c.passed} icon="✓" tone="border-ok/70 text-ok" />
          <Tile label="Failed" value={c.failed} icon="✕" tone="border-bad/70 text-bad" />
          <Tile label="Inconclusive" value={c.inconclusive} icon="?" tone="border-sun text-warn" />
          <Tile label="NotApplicable" value={c.notApplicable} icon="–" tone="border-ink-500 text-unknown" />
        </div>
        <p className="mt-2 text-sm text-muted">
          {tr.report.coverage(applicable, applicable + c.notApplicable)} {report.requestCount} istek gözlendi
          {report.rejectedCount > 0 && `, ${report.rejectedCount} istek platform tarafından reddedildi`}. {tr.report.noScore}
        </p>
      </section>

      <section aria-labelledby="checks">
        <h2 id="checks" className="mb-2 text-lg font-bold">
          {tr.report.checks}
        </h2>
        <ul className="flex flex-col gap-3">
          {report.checks.map((check) => (
            <li key={check.id}>
              <Card as="article">
                <div className="flex flex-wrap items-center gap-2">
                  <CheckBadge status={check.status} />
                  <EvidenceTag kind={check.evidenceKind} />
                  <h3 className="font-semibold">{check.title}</h3>
                  <code className="font-mono text-xs text-muted">{check.id}</code>
                </div>
                <dl className="mt-2 grid gap-1 text-sm sm:grid-cols-[8rem_1fr]">
                  <dt className="text-muted">{tr.report.expected}</dt>
                  <dd>{check.expected}</dd>
                  <dt className="text-muted">{tr.report.observed}</dt>
                  <dd>{check.observed}</dd>
                  <dt className="text-muted">Açıklama</dt>
                  <dd>{check.explanation}</dd>
                  {check.evidence.length > 0 && (
                    <>
                      <dt className="text-muted">{tr.report.evidence}</dt>
                      <dd className="font-mono text-xs">{check.evidence.slice(0, 20).map((e) => `#${e}`).join(", ")}</dd>
                    </>
                  )}
                </dl>
                {check.suggestion && check.status !== "Passed" && (
                  <p className="mt-2 rounded-md border border-accent-500/40 bg-accent-500/10 p-2 text-sm">
                    <span className="font-semibold">{tr.report.suggestion}: </span>
                    {check.suggestion}
                  </p>
                )}
              </Card>
            </li>
          ))}
        </ul>
      </section>

      <section aria-labelledby="unverifiable" className="rounded-xl border border-warn/40 bg-warn/5 p-4">
        <h2 id="unverifiable" className="mb-2 text-lg font-bold">
          {tr.report.unverifiable}
        </h2>
        <ul className="list-disc space-y-1 pl-5 text-sm text-fog-200">
          {report.unverifiable.map((u, index) => (
            <li key={`${index}-${u}`}>{u}</li>
          ))}
        </ul>
        <p className="mt-2 text-xs text-muted">{report.notice}</p>
      </section>

      {report.deliveries.length > 0 && (
        <section aria-labelledby="deliveries">
          <h2 id="deliveries" className="mb-2 text-lg font-bold">
            Webhook teslimleri (plan ↔ gerçekleşen)
          </h2>
          <div tabIndex={0} className="relative overflow-x-auto rounded-lg border border-ink-600">
            <table className="w-full min-w-[560px] text-sm">
              <thead className="bg-ink-800 text-left text-xs uppercase text-muted">
                <tr>
                  <th scope="col" className="p-2">Plan sırası</th>
                  <th scope="col" className="p-2">Event</th>
                  <th scope="col" className="p-2">Sürüm</th>
                  <th scope="col" className="p-2">Deneme</th>
                  <th scope="col" className="p-2">Gönderildi</th>
                  <th scope="col" className="p-2">Yanıt</th>
                </tr>
              </thead>
              <tbody>
                {report.deliveries.map((d) => (
                  <tr key={d.deliveryId} className="border-t border-ink-700">
                    <td className="p-2">{d.plannedOrder}</td>
                    <td className="p-2 font-mono text-xs">{publicView ? "—" : d.eventId}</td>
                    <td className="p-2">v{d.eventVersion}</td>
                    <td className="p-2">{d.attemptNumber}</td>
                    <td className="p-2 text-xs">{d.sentAt ? formatDateTime(d.sentAt) : "—"}</td>
                    <td className="p-2">
                      {d.responseStatus ?? "—"} <span className="text-xs text-muted">({d.outcome})</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {report.timeline.length > 0 && (
        <section aria-labelledby="timeline">
          <h2 id="timeline" className="mb-2 text-lg font-bold">
            {tr.report.timeline}
          </h2>
          <div tabIndex={0} className="relative overflow-x-auto rounded-lg border border-ink-600">
            <table className="w-full min-w-[560px] text-sm">
              <caption className="sr-only">{tr.report.timeline}</caption>
              <thead className="bg-ink-800 text-left text-xs uppercase text-muted">
                <tr>
                  <th scope="col" className="p-2">#</th>
                  <th scope="col" className="p-2">+ms</th>
                  <th scope="col" className="p-2">İstek</th>
                  <th scope="col" className="p-2">Deneme</th>
                  <th scope="col" className="p-2">Yanıt</th>
                  <th scope="col" className="p-2">Sonuç</th>
                </tr>
              </thead>
              <tbody>
                {report.timeline.map((t) => (
                  <tr key={t.sequence} className="border-t border-ink-700">
                    <td className="p-2 font-mono">{t.sequence}</td>
                    <td className="p-2 font-mono text-xs">{t.offsetMs}</td>
                    <td className="max-w-[14rem] truncate p-2 font-mono text-xs">
                      {t.method} /{t.route}
                      {!publicView && t.operationKey && <div className="truncate text-muted">key: {t.operationKey}</div>}
                    </td>
                    <td className="p-2">{t.attempt || "—"}</td>
                    <td className="p-2">
                      <HttpStatus code={t.plannedStatus} simulated={t.outcome !== "Rejected" && t.plannedStatus >= 400} rejected={t.outcome === "Rejected"} />
                    </td>
                    <td className="p-2 text-xs">
                      <OutcomeText outcome={t.outcome} /> · {formatDuration(t.durationMs)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {report.timelineTruncated && <p className="mt-1 text-xs text-muted">Zaman çizelgesi kısaltıldı.</p>}
        </section>
      )}
    </div>
  );
}
