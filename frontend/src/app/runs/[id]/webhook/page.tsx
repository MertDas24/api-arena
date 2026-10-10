"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { RequireAuth } from "@/components/require-auth";
import { RunStatusBadge } from "@/components/status";
import { Alert, Button, Card, EmptyState, ErrorState, PageHeader, Spinner } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { formatDuration, formatTime } from "@/lib/format";
import type { Run } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import type { Delivery } from "@/lib/webhook-types";

const outcomeText: Record<Delivery["outcome"], string> = {
  Pending: "… Bekliyor",
  Delivered: "✓ Teslim edildi (2xx)",
  Failed: "✕ Başarısız",
  TimedOut: "⌛ Zaman aşımı",
  Blocked: "⊘ Engellendi",
  Cancelled: "⊘ İptal",
  Unknown: "? Bilinmiyor",
};

function WebhookRun() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const run = useApi<Run>(`/api/runs/${id}`);
  const deliveries = useApi<Delivery[]>(`/api/runs/${id}/deliveries`);
  const [error, setError] = useState<string | null>(null);
  const active = run.data?.status === "Running";
  const reloadDeliveries = deliveries.reload;
  const reloadRun = run.reload;

  useEffect(() => {
    if (!active) return;
    const t = setInterval(() => {
      reloadDeliveries();
      reloadRun();
    }, 2000);
    return () => clearInterval(t);
  }, [active, reloadDeliveries, reloadRun]);

  if (run.loading) return <Spinner label="Yükleniyor…" />;
  if (run.error || !run.data) return <ErrorState error={run.error} onRetry={run.reload} />;
  const r = run.data;
  const ended = !["Created", "Running"].includes(r.status);
  const sentOrder = [...(deliveries.data ?? [])].filter((d) => d.sentAt).sort((a, b) => a.sentAt!.localeCompare(b.sentAt!));

  async function act(path: "finish" | "cancel") {
    setError(null);
    try {
      await api(`/api/runs/${id}/${path}`, { method: "POST" });
      router.push(`/runs/${id}/report`);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "İşlem başarısız.");
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
            · <code className="font-mono">{r.scenarioSlug}</code>
          </>
        }
        actions={
          ended ? (
            <Link href={`/runs/${id}/report`} className="inline-flex min-h-10 items-center rounded-md bg-accent-500 px-4 py-2 text-sm font-semibold text-white">
              Raporu aç →
            </Link>
          ) : (
            <>
              {r.status === "Created" && (
                <Button
                  onClick={async () => {
                    try {
                      await api(`/api/runs/${id}/start`, { method: "POST" });
                      run.reload();
                      deliveries.reload();
                    } catch (e) {
                      setError(e instanceof ApiError ? e.message : "Başlatılamadı.");
                    }
                  }}
                >
                  Testi başlat
                </Button>
              )}
              {r.status === "Running" && (
                <ConfirmDialog title="Koşuyu bitir" confirmLabel="Koşuyu bitir" onConfirm={() => act("finish")} trigger={(open) => <Button onClick={open}>Koşuyu bitir</Button>}>
                  Bekleyen teslimler iptal edilir; worker adapter&apos;dan son durumu okuyup raporu üretir.
                </ConfirmDialog>
              )}
              <ConfirmDialog title="Koşuyu iptal et" confirmLabel="İptal et" danger onConfirm={() => act("cancel")} trigger={(open) => <Button variant="secondary" onClick={open}>İptal et</Button>}>
                İptal edilen koşuda eksik kanıt Inconclusive sayılır.
              </ConfirmDialog>
            </>
          )
        }
      />
      {error && <Alert tone="error">{error}</Alert>}
      <Card className="flex flex-wrap items-center gap-3">
        <RunStatusBadge status={r.status} />
        <span className="text-sm text-muted">Teslimler worker tarafından planlı zamanda gönderilir; ekran 2 sn&apos;de bir güncellenir.</span>
      </Card>

      <section aria-labelledby="plan">
        <h2 id="plan" className="mb-2 font-semibold">
          Teslim planı ve sonuçları
        </h2>
        {deliveries.data?.length === 0 ? (
          <EmptyState title={r.status === "Created" ? "Koşu başlayınca teslim planı oluşur." : "Teslim yok."} />
        ) : (
          <div tabIndex={0} className="relative overflow-x-auto rounded-lg border border-ink-600">
            <table className="w-full min-w-[640px] text-sm">
              <caption className="sr-only">Teslim planı</caption>
              <thead className="bg-ink-800 text-left text-xs uppercase text-muted">
                <tr>
                  <th scope="col" className="p-2">Plan sırası</th>
                  <th scope="col" className="p-2">Event</th>
                  <th scope="col" className="p-2">Sürüm</th>
                  <th scope="col" className="p-2">Deneme</th>
                  <th scope="col" className="p-2">Planlanan</th>
                  <th scope="col" className="p-2">Gönderildi</th>
                  <th scope="col" className="p-2">Sonuç</th>
                </tr>
              </thead>
              <tbody>
                {deliveries.data?.map((d) => (
                  <tr key={d.deliveryId} className="border-t border-ink-700">
                    <td className="p-2">{d.plannedOrder}</td>
                    <td className="p-2 font-mono text-xs">{d.eventId}</td>
                    <td className="p-2">v{d.eventVersion}</td>
                    <td className="p-2">{d.attemptNumber}</td>
                    <td className="p-2 font-mono text-xs">{formatTime(d.scheduledAt)}</td>
                    <td className="p-2 font-mono text-xs">{d.sentAt ? formatTime(d.sentAt) : "—"}</td>
                    <td className="p-2 text-xs">
                      {outcomeText[d.outcome]} {d.responseStatus && `· ${d.responseStatus}`} {d.durationMs !== null && `· ${formatDuration(d.durationMs)}`}
                      {d.error && <code className="block font-mono text-muted">{d.error}</code>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {sentOrder.length > 0 && (
          <p className="mt-2 text-sm text-muted">
            Gerçekleşen gönderim sırası: {sentOrder.map((d) => `v${d.eventVersion}#${d.attemptNumber}`).join(" → ")}
          </p>
        )}
      </section>
      <Alert tone="info">HTTP 2xx yanıtı, olayın iş mantığında tek kez uygulandığının kanıtı değildir. Bunun için raporda adapter sonucuna bakın.</Alert>
    </div>
  );
}

export default function WebhookRunPage() {
  return (
    <RequireAuth>
      <WebhookRun />
    </RequireAuth>
  );
}
