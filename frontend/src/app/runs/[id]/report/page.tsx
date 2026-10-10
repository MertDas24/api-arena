"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { ReportView } from "@/components/report-view";
import { RequireAuth } from "@/components/require-auth";
import { SharePanel } from "@/components/share-panel";
import { Alert, Button, Card, ErrorState, PageHeader, Spinner } from "@/components/ui";
import { ApiError, download } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { Paged, Report, RunListItem } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function ComparePicker({ report }: { report: Report }) {
  const runs = useApi<Paged<RunListItem>>(`/api/projects/${report.projectId}/runs?pageSize=50`);
  const candidates = runs.data?.items.filter((r) => r.hasReport && r.id !== report.runId) ?? [];
  const [other, setOther] = useState("");
  if (candidates.length === 0) return <p className="text-sm text-muted">Karşılaştırmak için aynı projede raporu olan başka bir koşu gerekli.</p>;
  return (
    <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
      <div className="flex flex-col gap-1">
        <label htmlFor="cmp" className="text-sm">
          {tr.compare.pick}
        </label>
        <select id="cmp" value={other} onChange={(e) => setOther(e.target.value)} className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
          <option value="">—</option>
          {candidates.map((r) => (
            <option key={r.id} value={r.id}>
              {r.scenarioSlug} {r.label ? `· ${r.label}` : ""} · {formatDateTime(r.createdAt)}
            </option>
          ))}
        </select>
      </div>
      {other && (
        <Link className="inline-flex min-h-10 items-center rounded-md border border-ink-500 px-4 text-sm font-semibold hover:bg-ink-800" href={`/compare?left=${other}&right=${report.runId}`}>
          {tr.compare.run} →
        </Link>
      )}
    </div>
  );
}

function ReportPage() {
  const { id } = useParams<{ id: string }>();
  const { data, error, loading, reload } = useApi<Report>(`/api/runs/${id}/report`);
  const [exportError, setExportError] = useState<string | null>(null);
  const pending = error instanceof ApiError && error.code === "report_pending";

  useEffect(() => {
    if (!pending) return;
    const t = setTimeout(reload, 2000);
    return () => clearTimeout(t);
  }, [pending, reload]);

  if (loading || pending) return <Spinner label={pending ? "Rapor hazırlanıyor…" : tr.common.loading} />;
  if (error instanceof ApiError && error.code === "run_not_finished")
    return (
      <Alert tone="info" title={tr.report.pending}>
        <Link className="underline" href={`/runs/${id}`}>
          {tr.live.title} →
        </Link>
      </Alert>
    );
  if (error || !data) return <ErrorState error={error} onRetry={reload} />;

  async function exportAs(format: "json" | "html") {
    setExportError(null);
    try {
      await download(`/api/runs/${id}/export?format=${format}`, `apiarena-report.${format}`);
    } catch (e) {
      setExportError(e instanceof ApiError ? e.message : tr.common.errorTitle);
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={`${tr.report.title}: ${data.scenarioTitle}`}
        description={
          <>
            <Link className="underline" href={`/projects/${data.projectId}`}>
              {data.projectName}
            </Link>
            {data.label && ` · ${data.label}`}
          </>
        }
        actions={
          <>
            <Button variant="secondary" onClick={() => exportAs("json")}>
              {tr.report.exportJson}
            </Button>
            <Button variant="secondary" onClick={() => exportAs("html")}>
              {tr.report.exportHtml}
            </Button>
          </>
        }
      />
      {exportError && <Alert tone="error">{exportError}</Alert>}
      <ReportView report={data} />
      <Card>
        <h2 className="mb-2 font-semibold">{tr.report.compare}</h2>
        <ComparePicker report={data} />
      </Card>
      <SharePanel runId={data.runId} />
    </div>
  );
}

export default function RunReportPage() {
  return (
    <RequireAuth>
      <ReportPage />
    </RequireAuth>
  );
}
