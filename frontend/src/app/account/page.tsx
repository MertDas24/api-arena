"use client";

import { useRouter } from "next/navigation";
import { useState } from "react";
import { AccountDeletion } from "@/components/account-deletion";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { DevMailboxHint } from "@/components/dev-mailbox-hint";
import { RequireAuth } from "@/components/require-auth";
import { Alert, Button, Card, PageHeader } from "@/components/ui";
import { api, ApiError, resetCsrf } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { useSession } from "@/lib/session";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function Account() {
  const { me, setMe } = useSession();
  const features = useApi<{ retention: { observationDays: number; reportDays: number; deletedProjectDays: number } }>("/api/features");
  const router = useRouter();
  const [message, setMessage] = useState<{ tone: "success" | "error"; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  if (!me) return null;

  async function resend() {
    setBusy(true);
    setMessage(null);
    try {
      const r = await api<{ message: string }>("/api/auth/resend-verification", { method: "POST" });
      setMessage({ tone: "success", text: r.message });
    } catch (e) {
      setMessage({ tone: "error", text: e instanceof ApiError && e.status === 429 ? tr.auth.rateLimited(e.retryAfterSeconds) : e instanceof ApiError ? e.message : tr.common.errorTitle });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={tr.account.title} />
      {message && <Alert tone={message.tone}>{message.text}</Alert>}
      <Card>
        <dl className="grid gap-2 text-sm sm:grid-cols-[10rem_1fr]">
          <dt className="text-muted">{tr.account.email}</dt>
          <dd className="break-all">{me.email}</dd>
          <dt className="text-muted">Durum</dt>
          <dd>{me.emailConfirmed ? `✓ ${tr.account.verified}` : `! ${tr.account.notVerified}`}</dd>
          <dt className="text-muted">Oluşturma</dt>
          <dd>{formatDateTime(me.createdAt)}</dd>
        </dl>
        {!me.emailConfirmed && (
          <div className="mt-3">
            <Button onClick={resend} busy={busy}>
              {tr.auth.resend}
            </Button>
            <DevMailboxHint email={me.email} />
          </div>
        )}
      </Card>

      <Card>
        <h2 className="mb-1 font-semibold">{tr.account.logoutAll}</h2>
        <p className="mb-3 text-sm text-muted">{tr.account.logoutAllHint}</p>
        <ConfirmDialog
          title={tr.account.logoutAll}
          confirmLabel={tr.account.logoutAll}
          onConfirm={async () => {
            await api("/api/auth/logout-all", { method: "POST" });
            resetCsrf();
            setMe(null);
            router.push("/login");
          }}
          trigger={(open) => (
            <Button variant="secondary" onClick={open}>
              {tr.account.logoutAll}
            </Button>
          )}
        >
          {tr.account.logoutAllHint}
        </ConfirmDialog>
      </Card>

      <Card>
        <h2 className="mb-2 font-semibold">{tr.account.dataPolicy}</h2>
        <ul className="list-disc space-y-1 pl-5 text-sm text-fog-300">
          <li>{features.data ? `İstek kayıtları koşu bittikten ${features.data.retention.observationDays} gün, raporlar ${features.data.retention.reportDays} gün sonra; silinen projeler ${features.data.retention.deletedProjectDays} gün sonra otomatik silinir (worker temizlik işi).` : "Saklama süreleri yükleniyor…"}</li>
          <li>{tr.account.sharingPending}</li>
          <li>{tr.account.keyStorage}</li>
          <li>Hesap silindiğinde aktif koşular iptal edilir, anahtarlar ve paylaşımlar geçersiz olur, veriler veritabanından silinir. Yedeklerden silinme yedek saklama süresi kadar gecikebilir.</li>
          <li>Bu metin hukuki uygunluk garantisi değildir; yayın öncesi uzman incelemesi gerekir.</li>
        </ul>
      </Card>

      <AccountDeletion />
    </div>
  );
}

export default function AccountPage() {
  return (
    <RequireAuth>
      <Account />
    </RequireAuth>
  );
}
