"use client";

import { useParams } from "next/navigation";
import { ReportView } from "@/components/report-view";
import { Alert, PageHeader, Spinner } from "@/components/ui";
import { formatDateTime } from "@/lib/format";
import type { Report } from "@/lib/types";
import { useApi } from "@/lib/use-api";

type PublicReport = Omit<Report, "reportId" | "runId" | "projectId" | "projectName" | "label" | "createdAt" | "startedAt" | "endedAt" | "engine" | "incompleteReason" | "inboundLimit"> & {
  shareExpiresAt: string;
};

/** Anonymous, read-only view of a sanitised report. Response carries X-Robots-Tag noindex; page sends no referrer. */
export default function SharedReportPage() {
  const { token } = useParams<{ token: string }>();
  const { data, error, loading } = useApi<PublicReport>(`/api/public/shares/${encodeURIComponent(token)}`);
  if (loading) return <Spinner label="Yükleniyor…" />;
  if (error || !data)
    return (
      <Alert tone="error" title="Paylaşım bulunamadı">
        Bağlantı geçersiz, süresi dolmuş ya da sahibi tarafından iptal edilmiş olabilir.
      </Alert>
    );

  const report: Report = {
    ...data,
    reportId: "",
    runId: "",
    projectId: "",
    projectName: "",
    label: null,
    createdAt: data.generatedAt,
    startedAt: null,
    endedAt: null,
    engine: "",
    incompleteReason: data.incompleteReasonText ? "x" : null,
    inboundLimit: 0,
    timeline: data.timeline.map((t) => ({ ...t, receivedAt: data.generatedAt, operationKey: null, rejectReason: null })),
    deliveries: data.deliveries.map((d, i) => ({ ...d, deliveryId: String(i), eventId: "" })),
  };

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={`Paylaşılan rapor: ${data.scenarioTitle}`} description={`Salt okunur, maskeli özet · bağlantı ${formatDateTime(data.shareExpiresAt)} tarihine kadar geçerli`} />
      <ReportView report={report} publicView />
    </div>
  );
}
