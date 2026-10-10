"use client";

import Link from "next/link";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { ConnectionSnippets } from "@/components/connection-snippets";
import { RequireAuth } from "@/components/require-auth";
import { ScenarioDetails } from "@/components/scenario-details";
import { SecretOnce } from "@/components/secret-once";
import { Alert, Button, Card, CodeBlock, ErrorState, Field, PageHeader, Spinner } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { useSession } from "@/lib/session";
import type { CreatedRunKey, Project, Run, Scenario } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { useUnsavedChangesWarning } from "@/lib/use-unsaved";
import { tr } from "@/lib/i18n/tr";

import { WEBHOOK_ENGINES, type WebhookTarget } from "@/lib/webhook-types";

function Steps({ current }: { current: 1 | 2 | 3 }) {
  const steps = [tr.wizard.step1, tr.wizard.step2, tr.wizard.step3];
  return (
    <ol className="mb-6 grid gap-2 sm:grid-cols-3" aria-label="Adımlar">
      {steps.map((s, i) => {
        const n = (i + 1) as 1 | 2 | 3;
        const state = n < current ? "done" : n === current ? "current" : "todo";
        return (
          <li
            key={s}
            aria-current={state === "current" ? "step" : undefined}
            className={`rounded-lg border px-3 py-2 text-sm ${state === "current" ? "border-accent-500 bg-accent-500/15 font-semibold" : state === "done" ? "border-ok/50 text-fog-200" : "border-ink-600 text-muted"}`}
          >
            <span aria-hidden>{state === "done" ? "✓ " : `${n}. `}</span>
            <span className="sr-only">{state === "done" ? "Tamamlandı: " : state === "current" ? "Şu anki adım: " : "Sıradaki: "}</span>
            {s}
          </li>
        );
      })}
    </ol>
  );
}

function StepChoose({ projectId, onCreated }: { projectId: string; onCreated: (run: Run) => void }) {
  const scenarios = useApi<Scenario[]>("/api/scenarios");
  const features = useApi<{ webhooks: boolean }>("/api/features");
  const targets = useApi<WebhookTarget[]>(features.data?.webhooks ? `/api/projects/${projectId}/webhook-targets` : null);
  const [targetId, setTargetId] = useState("");
  const [slug, setSlug] = useState<string | null>(null);
  const [label, setLabel] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useUnsavedChangesWarning(slug !== null || label !== "");

  if (scenarios.loading) return <Spinner label={tr.common.loading} />;
  if (scenarios.error || !scenarios.data) return <ErrorState error={scenarios.error} onRetry={scenarios.reload} />;
  const http = scenarios.data;
  const selected = http.find((s) => (s.kind === "Custom" ? s.id : s.slug) === slug);
  const isWebhook = !!selected && WEBHOOK_ENGINES.includes(selected.latest.engine);
  const verified = (targets.data ?? []).filter((t) => t.status === "Verified");

  async function create() {
    if (!selected) return;
    setBusy(true);
    setError(null);
    try {
      const run = await api<Run>(`/api/projects/${projectId}/runs`, { method: "POST", body: { scenario: selected.kind === "Custom" ? selected.id : selected.slug, label: label || null, webhookTargetId: isWebhook ? targetId : null } });
      onCreated(run);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : tr.common.errorTitle);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <fieldset>
        <legend className="mb-2 font-semibold">{tr.wizard.step1}</legend>
        <div className="grid gap-3 md:grid-cols-3">
          {http.map((s) => {
            const value = s.kind === "Custom" ? s.id : s.slug;
            return (
              <label key={s.id} className={`flex cursor-pointer flex-col gap-1 rounded-xl border p-4 ${slug === value ? "border-accent-500 bg-accent-500/10" : "border-ink-600 bg-ink-850 hover:border-ink-500"}`}>
                <span className="flex items-center gap-2">
                  <input type="radio" name="scenario" value={value} checked={slug === value} onChange={() => setSlug(value)} className="h-4 w-4 accent-accent-500" />
                  <span className="font-semibold">{s.title}</span>
                </span>
                <code className="font-mono text-xs text-accent-300">{s.slug}</code>
                <span className="text-sm text-muted">{s.summary}</span>
              </label>
            );
          })}
        </div>
      </fieldset>
      {selected && (
        <Card>
          <ScenarioDetails version={selected.latest} />
        </Card>
      )}
      {isWebhook && (
        <div className="flex flex-col gap-1">
          <label htmlFor="target" className="text-sm font-medium">
            Doğrulanmış webhook hedefi
          </label>
          {verified.length === 0 ? (
            <Alert tone="warning">
              Bu proje için doğrulanmış hedef yok.{" "}
              <Link className="underline" href={`/projects/${projectId}/webhooks`}>
                Webhook hedefi ekle ve doğrula
              </Link>
            </Alert>
          ) : (
            <select id="target" value={targetId} onChange={(e) => setTargetId(e.target.value)} className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
              <option value="">Seçin…</option>
              {verified.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.url}
                </option>
              ))}
            </select>
          )}
        </div>
      )}
      <Field label={`${tr.wizard.label} (${tr.common.optional})`} hint={tr.wizard.labelHint} value={label} maxLength={80} onChange={(e) => setLabel(e.target.value)} />
      {error && <Alert tone="error">{error}</Alert>}
      <div>
        <Button disabled={!selected || (isWebhook && !targetId)} busy={busy} onClick={create}>
          {tr.wizard.createRun}
        </Button>
      </div>
    </div>
  );
}

function StepConnect({ run, onNext }: { run: Run; onNext: () => void }) {
  const [key, setKey] = useState<CreatedRunKey | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const hasActive = run.keys.some((k) => k.active);

  async function generate() {
    setBusy(true);
    setError(null);
    try {
      setKey(await api<CreatedRunKey>(`/api/runs/${run.id}/access-keys`, { method: "POST" }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : tr.common.errorTitle);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <h2 className="font-semibold">{tr.wizard.step2}</h2>
      <div className="grid gap-3 lg:grid-cols-2">
        <CodeBlock label={tr.live.baseUrl} code={run.simulatorBaseUrl} />
        {run.simulatorUrl && <CodeBlock label={`${tr.live.fullUrl} (${run.method})`} code={run.simulatorUrl} />}
      </div>
      {key ? (
        <SecretOnce value={key.key} label="Run key" />
      ) : (
        <div className="flex flex-col gap-2">
          {hasActive && <Alert tone="info">Bu koşu için zaten bir anahtar oluşturulmuş. Elinde yoksa yenisini oluştur; eskisi iptal olur.</Alert>}
          <div>
            <Button onClick={generate} busy={busy}>
              {tr.wizard.generateKey}
            </Button>
          </div>
        </div>
      )}
      {error && <Alert tone="error">{error}</Alert>}
      <ConnectionSnippets baseUrl={run.simulatorBaseUrl} scenario={run.scenarioSlug} />
      <div>
        <Button onClick={onNext} disabled={!key && !hasActive}>
          {key ? tr.wizard.keySaved : tr.common.next} →
        </Button>
      </div>
    </div>
  );
}

function StepStart({ run }: { run: Run }) {
  const router = useRouter();
  const { me } = useSession();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const blocked = !!me && me.runsRequireVerifiedEmail && !me.emailConfirmed;
  return (
    <div className="flex flex-col gap-4">
      <h2 className="font-semibold">{tr.wizard.step3}</h2>
      <p className="text-sm text-muted">{tr.wizard.startHint(Math.round(run.ttlSeconds / 60))}</p>
      {blocked && (
        <Alert tone="warning">
          {tr.wizard.unverified}{" "}
          <Link className="underline" href="/account">
            {tr.nav.account}
          </Link>
        </Alert>
      )}
      {error && <Alert tone="error">{error}</Alert>}
      <div>
        <Button
          disabled={blocked}
          busy={busy}
          onClick={async () => {
            setBusy(true);
            setError(null);
            try {
              await api<Run>(`/api/runs/${run.id}/start`, { method: "POST" });
              router.push(`/runs/${run.id}`);
            } catch (e) {
              setError(e instanceof ApiError ? e.message : tr.common.errorTitle);
              setBusy(false);
            }
          }}
        >
          {tr.wizard.start}
        </Button>
      </div>
    </div>
  );
}

function Wizard() {
  const { id } = useParams<{ id: string }>();
  const params = useSearchParams();
  const router = useRouter();
  const project = useApi<Project>(`/api/projects/${id}`);
  const runId = params.get("run");
  const step = (Number(params.get("step")) || (runId ? 2 : 1)) as 1 | 2 | 3;
  const run = useApi<Run>(runId ? `/api/runs/${runId}` : null);

  if (project.loading) return <Spinner label={tr.common.loading} />;
  if (project.error || !project.data) return <ErrorState error={project.error} onRetry={project.reload} />;

  return (
    <div>
      <PageHeader title={tr.wizard.title} description={project.data.name} />
      <Steps current={step} />
      <Card>
        {step === 1 && (
          <StepChoose
            projectId={id}
            onCreated={(r) => router.replace(r.webhookTargetId ? `/runs/${r.id}/webhook` : `/projects/${id}/runs/new?run=${r.id}&step=2`)}
          />
        )}
        {step > 1 && run.loading && <Spinner label={tr.common.loading} />}
        {step > 1 && run.error ? <ErrorState error={run.error} onRetry={run.reload} /> : null}
        {step === 2 && run.data && <StepConnect run={run.data} onNext={() => router.replace(`/projects/${id}/runs/new?run=${run.data!.id}&step=3`)} />}
        {step === 3 && run.data && (run.data.status === "Created" ? <StepStart run={run.data} /> : <Alert tone="info"><Link className="underline" href={`/runs/${run.data.id}`}>{tr.live.title} →</Link></Alert>)}
      </Card>
    </div>
  );
}

export default function NewRunPage() {
  return (
    <RequireAuth>
      <Suspense>
        <Wizard />
      </Suspense>
    </RequireAuth>
  );
}
