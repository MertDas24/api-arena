"use client";

import { useState } from "react";
import { ConfirmDialog } from "./confirm-dialog";
import { Alert, Button, Card, CopyButton } from "./ui";
import { api, ApiError } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { useApi } from "@/lib/use-api";

interface Share {
  id: string;
  prefix: string;
  createdAt: string;
  expiresAt: string;
  revokedAt: string | null;
  active: boolean;
  viewCount: number;
}

/**
 * Read-only, expiring share links. The full link is shown once (only a hash is stored). Shared reports exclude the
 * project name, labels, operation keys, headers, bodies and e-mail addresses.
 */
export function SharePanel({ runId }: { runId: string }) {
  const shares = useApi<Share[]>(`/api/runs/${runId}/shares`);
  const [days, setDays] = useState(7);
  const [created, setCreated] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function create() {
    setError(null);
    try {
      const r = await api<{ url: string }>(`/api/runs/${runId}/shares`, { method: "POST", body: { expiresInDays: days } });
      setCreated(r.url);
      shares.reload();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Paylaşım oluşturulamadı.");
    }
  }

  return (
    <Card>
      <h2 className="mb-1 font-semibold">Salt okunur paylaşım</h2>
      <p className="mb-3 text-sm text-muted">
        Bağlantıya sahip herkes raporun maskeli özetini görebilir: kontroller, sayılar ve zaman çizelgesi. Proje adı, etiket, Idempotency-Key, başlıklar, gövdeler ve
        e-posta paylaşılmaz. Arama motorlarına kapalıdır (noindex) ve istediğin an iptal edebilirsin.
      </p>
      {error && <Alert tone="error">{error}</Alert>}
      {created && (
        <div className="mb-3 flex flex-col gap-2">
          <Alert tone="warning">Bu bağlantı yalnızca şimdi gösterilir. Kopyala; kaybedersen yenisini oluştur.</Alert>
          <div className="flex flex-col gap-2 rounded-md border border-ink-500 bg-ink-950 p-2 sm:flex-row sm:items-center">
            <code className="min-w-0 flex-1 break-all font-mono text-xs">{created}</code>
            <CopyButton value={created} />
          </div>
        </div>
      )}
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex flex-col gap-1">
          <label htmlFor="share-days" className="text-sm">
            Geçerlilik
          </label>
          <select id="share-days" value={days} onChange={(e) => setDays(Number(e.target.value))} className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
            <option value={1}>1 gün</option>
            <option value={7}>7 gün</option>
            <option value={30}>30 gün</option>
          </select>
        </div>
        <Button onClick={create}>Paylaşım bağlantısı oluştur</Button>
      </div>
      {shares.data && shares.data.length > 0 && (
        <ul className="mt-3 flex flex-col gap-2 text-sm">
          {shares.data.map((s) => (
            <li key={s.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-ink-600 p-2">
              <span>
                <code className="font-mono">{s.prefix}…</code> · {s.active ? `✓ aktif, bitiş ${formatDateTime(s.expiresAt)}` : s.revokedAt ? "⊘ iptal edildi" : "⌛ süresi doldu"} ·{" "}
                {s.viewCount} görüntülenme
              </span>
              {s.active && (
                <ConfirmDialog
                  title="Paylaşımı iptal et"
                  confirmLabel="İptal et"
                  danger
                  onConfirm={async () => {
                    await api(`/api/runs/${runId}/shares/${s.id}`, { method: "DELETE" });
                    shares.reload();
                  }}
                  trigger={(open) => (
                    <Button variant="ghost" className="min-h-8 py-1" onClick={open}>
                      İptal et
                    </Button>
                  )}
                >
                  Bağlantı hemen çalışmaz hale gelir.
                </ConfirmDialog>
              )}
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
