"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState, type FormEvent } from "react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { RequireAuth } from "@/components/require-auth";
import { SecretOnce } from "@/components/secret-once";
import { Alert, Button, Card, CodeBlock, EmptyState, ErrorState, Field, PageHeader, Spinner, fieldErrors } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { Project } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { verificationErrors, type CreatedWebhookTarget, type WebhookTarget } from "@/lib/webhook-types";

const statusView: Record<WebhookTarget["status"], { icon: string; text: string; cls: string }> = {
  Pending: { icon: "○", text: "Doğrulanmadı", cls: "border-unknown/60 text-fog-300" },
  Verified: { icon: "✓", text: "Doğrulandı", cls: "border-ok/60 text-ok" },
  VerificationFailed: { icon: "✕", text: "Doğrulama başarısız", cls: "border-bad/60 text-bad" },
  Revoked: { icon: "⊘", text: "Kaldırıldı", cls: "border-unknown/60 text-muted" },
};

function TargetCard({ projectId, target, onChange }: { projectId: string; target: WebhookTarget; onChange: () => void }) {
  const [secret, setSecret] = useState<string | null>(null);
  const [message, setMessage] = useState<{ tone: "info" | "error"; text: string } | null>(null);
  const v = statusView[target.status];
  const base = `/api/projects/${projectId}/webhook-targets/${target.id}`;

  async function call(fn: () => Promise<void>) {
    setMessage(null);
    try {
      await fn();
    } catch (e) {
      setMessage({ tone: "error", text: e instanceof ApiError ? e.message : "İşlem başarısız." });
    }
  }

  return (
    <Card as="article">
      <div className="flex flex-wrap items-center gap-2">
        <span className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-semibold ${v.cls}`}>
          <span aria-hidden>{v.icon}</span>
          {v.text}
        </span>
        {target.isDevLocal && <span className="rounded border border-warn/60 px-1.5 py-0.5 text-[11px] text-warn">Yalnızca yerel geliştirme</span>}
      </div>
      <p className="mt-2 break-all font-mono text-sm">{target.url}</p>
      <dl className="mt-2 grid gap-1 text-xs text-muted sm:grid-cols-[10rem_1fr]">
        <dt>İmza sırrı</dt>
        <dd className="font-mono">{target.secretHint} (yalnızca son karakterler)</dd>
        <dt>Adapter yolu</dt>
        <dd className="font-mono">{target.adapterPath ?? "— (iş sonucu Inconclusive olur)"}</dd>
        <dt>Doğrulama</dt>
        <dd>
          {target.verifiedAt ? `${formatDateTime(target.verifiedAt)} · geçerlilik ${formatDateTime(target.verificationExpiresAt)}` : "—"}
          {target.lastVerificationError && <span className="block text-bad">{verificationErrors[target.lastVerificationError] ?? target.lastVerificationError}</span>}
        </dd>
      </dl>
      {secret && (
        <div className="mt-3">
          <SecretOnce value={secret} label="İmza sırrı" />
        </div>
      )}
      {message && (
        <div className="mt-3">
          <Alert tone={message.tone}>{message.text}</Alert>
        </div>
      )}
      <div className="mt-3 flex flex-wrap gap-2">
        <ConfirmDialog
          title="Hedefi doğrula"
          confirmLabel="Doğrulama isteği gönder"
          onConfirm={() =>
            call(async () => {
              await api(`${base}/verify`, { method: "POST" });
              setMessage({ tone: "info", text: "Doğrulama kuyruğa alındı. Birkaç saniye içinde durum güncellenir." });
              setTimeout(onChange, 2500);
            })
          }
          trigger={(open) => <Button onClick={open}>Doğrula</Button>}
        >
          <p>
            API Arena bu adrese rastgele bir challenge içeren <strong>kontrollü bir POST</strong> gönderecek. Alıcı, imza sırrıyla HMAC yanıtı vermelidir. Doğrulandığında bu
            adrese senaryo teslimleri (koşu başına en fazla 10, saniyede en fazla 2) gönderilebilir. Doğrulama iç ağ adreslerine izin vermez.
          </p>
        </ConfirmDialog>
        <ConfirmDialog
          title="İmza sırrını yenile"
          confirmLabel="Yenile"
          onConfirm={() =>
            call(async () => {
              const r = await api<CreatedWebhookTarget>(`${base}/rotate-secret`, { method: "POST" });
              setSecret(r.signingSecret);
              onChange();
            })
          }
          trigger={(open) => (
            <Button variant="secondary" onClick={open}>
              Sırrı yenile
            </Button>
          )}
        >
          Eski sır hemen geçersiz olur ve hedef yeniden doğrulanmalıdır.
        </ConfirmDialog>
        <ConfirmDialog
          title="Hedefi kaldır"
          confirmLabel="Kaldır"
          danger
          onConfirm={() =>
            call(async () => {
              await api(base, { method: "DELETE" });
              onChange();
            })
          }
          trigger={(open) => (
            <Button variant="ghost" onClick={open}>
              Kaldır
            </Button>
          )}
        >
          Bekleyen teslimler iptal edilir. Geçmiş raporlar etkilenmez.
        </ConfirmDialog>
      </div>
    </Card>
  );
}

function CreateTarget({ projectId, onCreated }: { projectId: string; onCreated: (c: CreatedWebhookTarget) => void }) {
  const [url, setUrl] = useState("");
  const [adapterPath, setAdapterPath] = useState("/webhooks/apiarena/adapter");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onCreated(await api<CreatedWebhookTarget>(`/api/projects/${projectId}/webhook-targets`, { method: "POST", body: { url, adapterPath: adapterPath || null } }));
      setUrl("");
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  const general = error instanceof ApiError && Object.keys(error.fieldErrors).length === 0 ? error.message : null;
  return (
    <Card>
      <form className="flex flex-col gap-3" onSubmit={submit} noValidate>
        <h2 className="font-semibold">Webhook hedefi ekle</h2>
        {general && <Alert tone="error">{general}</Alert>}
        <Field label="Hedef URL (HTTPS)" placeholder="https://api.uygulaman.com/webhooks/apiarena" value={url} onChange={(e) => setUrl(e.target.value)} error={fieldErrors(error, "url")}
          hint="Yalnızca HTTPS, 443/8443 portları, alan adı (IP değil). Yerel geliştirmede örnek alıcı: http://localhost:5160/webhooks/apiarena" />
        <Field label="Read-only adapter yolu (isteğe bağlı)" value={adapterPath} onChange={(e) => setAdapterPath(e.target.value)} error={fieldErrors(error, "adapterPath")}
          hint="Aynı origin üzerinde. Yalnızca bu sentetik koşunun durumunu okur; adapter sonuçları 'Adapter bildirdi' olarak işaretlenir." />
        <div>
          <Button type="submit" busy={busy}>
            Hedef oluştur
          </Button>
        </div>
      </form>
    </Card>
  );
}

function Webhooks() {
  const { id } = useParams<{ id: string }>();
  const features = useApi<{ webhooks: boolean }>("/api/features");
  const project = useApi<Project>(`/api/projects/${id}`);
  const enabled = features.data?.webhooks ?? false;
  const targets = useApi<WebhookTarget[]>(enabled ? `/api/projects/${id}/webhook-targets` : null);
  const [created, setCreated] = useState<string | null>(null);

  if (features.loading || project.loading) return <Spinner label="Yükleniyor…" />;
  if (project.error || !project.data) return <ErrorState error={project.error} onRetry={project.reload} />;

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title="Webhook hedefleri" description={<Link className="underline" href={`/projects/${id}`}>{project.data.name}</Link>} />
      {!enabled ? (
        <Alert tone="info" title="Webhook modülü bu ortamda kapalı">
          Giden webhook özelliği varsayılan olarak kapalıdır. Production&apos;da yalnızca ağ çıkış (egress) kısıtları kurulup doğrulandıktan sonra açılır. Yerel demoda
          <code className="mx-1 break-all font-mono">ApiArena__Webhooks__Enabled=true</code> ve şifreleme anahtarı ile açabilirsiniz (README).
        </Alert>
      ) : (
        <>
          <Alert tone="warning">
            Webhook senaryolarında istekleri API Arena gönderir. Hedef URL&apos;nin size ait olduğunu HMAC challenge ile kanıtlamanız gerekir; doğrulama iç ağ adreslerine
            asla izin vermez ve her bağlantıda adres yeniden kontrol edilir.
          </Alert>
          {created && <SecretOnce value={created} label="İmza sırrı" />}
          {created && <CodeBlock label="Örnek alıcıyı bu sırla başlatın (PowerShell)" code={`$env:RECEIVER_SIGNING_SECRET = [System.Net.NetworkCredential]::new("", (Read-Host -AsSecureString "Signing secret")).Password\ndotnet run --project examples/webhook-receiver --urls http://localhost:5160`} />}
          <CreateTarget
            projectId={id}
            onCreated={(c) => {
              setCreated(c.signingSecret);
              targets.reload();
            }}
          />
          {targets.loading && <Spinner label="Yükleniyor…" />}
          {targets.error ? <ErrorState error={targets.error} onRetry={targets.reload} /> : null}
          {targets.data?.length === 0 && <EmptyState title="Henüz webhook hedefi yok." />}
          <div className="flex flex-col gap-3">
            {targets.data?.map((t) => (
              <TargetCard key={t.id} projectId={id} target={t} onChange={targets.reload} />
            ))}
          </div>
        </>
      )}
    </div>
  );
}

export default function WebhooksPage() {
  return (
    <RequireAuth>
      <Webhooks />
    </RequireAuth>
  );
}
