"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { ConfirmDialog } from "./confirm-dialog";
import { Alert, Button, Card, Field, TextArea, fieldErrors } from "./ui";
import { api, ApiError, download, resetCsrf } from "@/lib/api";
import type { Scenario } from "@/lib/types";
import { useUnsavedChangesWarning } from "@/lib/use-unsaved";

export interface StepForm {
  status: number;
  delayMs: number;
  bodyType: "none" | "json" | "text";
  body: string;
  retryAfter: string;
}

interface PreviewStep {
  attempt: number;
  status: number;
  delayMs: number;
  contentType: string;
  body: string;
  retryAfter: string | null;
}

const STATUSES = [200, 201, 202, 204, 400, 401, 403, 404, 409, 422, 429, 500, 502, 503, 504];
const METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE"];

export function toDefinition(method: string, route: string, steps: StepForm[]) {
  return {
    schemaVersion: 1,
    method,
    route,
    steps: steps.map((s) => ({
      status: s.status,
      delayMs: s.delayMs,
      ...(s.bodyType !== "none" ? { body: { type: s.bodyType, content: s.body } } : {}),
      ...(s.retryAfter.trim() ? { retryAfterSeconds: Number(s.retryAfter) } : {}),
    })),
  };
}

export function fromDefinition(def: Record<string, unknown>): { method: string; route: string; steps: StepForm[] } {
  const steps = (def.steps as Array<{ status: number; delayMs: number; body?: { type: "json" | "text"; content: string }; retryAfterSeconds?: number }>) ?? [];
  return {
    method: String(def.method ?? "GET"),
    route: String(def.route ?? ""),
    steps: steps.map((s) => ({
      status: s.status,
      delayMs: s.delayMs,
      bodyType: s.body?.type ?? "none",
      body: s.body?.content ?? "",
      retryAfter: s.retryAfterSeconds ? String(s.retryAfterSeconds) : "",
    })),
  };
}

const defaultSteps: StepForm[] = [
  { status: 503, delayMs: 0, bodyType: "json", body: '{"error":"temporarily_unavailable"}', retryAfter: "" },
  { status: 200, delayMs: 0, bodyType: "json", body: '{"ok":true}', retryAfter: "" },
];

/**
 * Declarative editor. The browser only helps; the server validates authoritatively and the preview is produced by
 * the simulator's own decision function, so what you see is what a real run returns.
 */
export function CustomScenarioEditor({ existing }: { existing?: Scenario }) {
  const router = useRouter();
  const initial = existing ? fromDefinition(existing.latest.definition) : { method: "POST", route: "orders/create", steps: defaultSteps };
  const [title, setTitle] = useState(existing?.title ?? "");
  const [summary, setSummary] = useState(existing?.summary ?? "");
  const [method, setMethod] = useState(initial.method);
  const [route, setRoute] = useState(initial.route);
  const [steps, setSteps] = useState<StepForm[]>(initial.steps);
  const [preview, setPreview] = useState<PreviewStep[] | null>(null);
  const [previewError, setPreviewError] = useState<ApiError | null>(null);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const [dirty, setDirty] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);
  useUnsavedChangesWarning(dirty);

  const definition = toDefinition(method, route, steps);
  const definitionKey = JSON.stringify(definition);

  useEffect(() => {
    const t = setTimeout(() => {
      api<PreviewStep[]>("/api/scenarios/custom/preview", { method: "POST", body: { definition: JSON.parse(definitionKey), attempts: null } })
        .then((p) => {
          setPreview(p);
          setPreviewError(null);
        })
        .catch((e: unknown) => {
          setPreview(null);
          setPreviewError(e instanceof ApiError ? e : null);
        });
    }, 400);
    return () => clearTimeout(t);
  }, [definitionKey]);

  function update(i: number, patch: Partial<StepForm>) {
    setSteps((s) => s.map((x, j) => (j === i ? { ...x, ...patch } : x)));
    setDirty(true);
  }

  async function save() {
    setBusy(true);
    setSaveError(null);
    try {
      const body = { title, summary, definition };
      const saved = existing
        ? await api<Scenario>(`/api/scenarios/custom/${existing.id}/versions`, { method: "POST", body })
        : await api<Scenario>("/api/scenarios/custom", { method: "POST", body });
      setDirty(false);
      router.push(`/scenarios/custom/${saved.id}?saved=${saved.latest.version}`);
      router.refresh();
    } catch (e) {
      setSaveError(e);
    } finally {
      setBusy(false);
    }
  }

  async function importFile(file: File) {
    setSaveError(null);
    if (file.size > 32 * 1024) {
      setSaveError(new ApiError({ status: 400, title: "Dosya en fazla 32 KiB olabilir." }));
      return;
    }
    try {
      const text = await file.text();
      const csrf = await (await fetch("/api/auth/csrf", { credentials: "same-origin" })).json();
      const res = await fetch("/api/scenarios/custom/import", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf.token },
        body: text,
      });
      resetCsrf();
      const json = await res.json();
      if (!res.ok) throw new ApiError({ ...json, status: res.status });
      router.push(`/scenarios/custom/${(json as Scenario).id}`);
    } catch (e) {
      setSaveError(e);
    }
  }

  const general = saveError instanceof ApiError ? saveError.message : saveError ? "Kaydedilemedi." : null;
  const defErrors = previewError ? Object.entries(previewError.fieldErrors) : [];

  return (
    <div className="flex flex-col gap-4">
      <Alert tone="info">
        Yalnızca sabit yanıtlar: durum kodu, gövde (JSON/metin), gecikme (≤ 5 sn) ve Retry-After. Kod, script, regex, ek başlık veya dış URL yoktur. En fazla 10 adım; son
        adımdan sonraki denemeler son adımı tekrarlar. Uygulamanın iş sonucu HTTP trafiğinden gözlenemez, raporda Inconclusive görünür.
      </Alert>
      {general && <Alert tone="error">{general}</Alert>}
      {dirty && <Alert tone="warning">Kaydedilmemiş değişiklikler var. Kaydettiğinde yeni bir sürüm oluşur; eski koşular ve raporlar değişmez.</Alert>}

      <Card className="flex flex-col gap-3">
        <Field label="Başlık" value={title} maxLength={120} onChange={(e) => (setTitle(e.target.value), setDirty(true))} error={fieldErrors(saveError, "title")} />
        <TextArea label="Özet (isteğe bağlı)" value={summary} maxLength={500} rows={2} onChange={(e) => (setSummary(e.target.value), setDirty(true))} />
        <div className="grid gap-3 sm:grid-cols-[8rem_1fr]">
          <div className="flex flex-col gap-1">
            <label htmlFor="method" className="text-sm font-medium">
              Metot
            </label>
            <select id="method" value={method} onChange={(e) => (setMethod(e.target.value), setDirty(true))} className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-2">
              {METHODS.map((m) => (
                <option key={m}>{m}</option>
              ))}
            </select>
          </div>
          <Field label="Route (SIM_BASE_URL'den sonra)" value={route} onChange={(e) => (setRoute(e.target.value), setDirty(true))} hint="Küçük harf, rakam ve '-'; en fazla 4 segment. Ör: orders/create" />
        </div>
      </Card>

      <section aria-labelledby="steps" className="flex flex-col gap-3">
        <h2 id="steps" className="font-semibold">
          Adımlar (deneme sırasına göre)
        </h2>
        {steps.map((s, i) => (
          <Card key={i} as="div" className="flex flex-col gap-3">
            <div className="flex flex-wrap items-end gap-3">
              <span className="font-mono text-sm text-accent-300">Deneme {i + 1}</span>
              <div className="flex flex-col gap-1">
                <label htmlFor={`status-${i}`} className="text-xs">
                  Durum
                </label>
                <select id={`status-${i}`} value={s.status} onChange={(e) => update(i, { status: Number(e.target.value) })} className="min-h-9 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
                  {STATUSES.map((c) => (
                    <option key={c}>{c}</option>
                  ))}
                </select>
              </div>
              <Field className="w-28" label="Gecikme (ms)" type="number" min={0} max={5000} value={s.delayMs} onChange={(e) => update(i, { delayMs: Number(e.target.value) })} />
              {(s.status === 429 || s.status === 503) && (
                <Field className="w-32" label="Retry-After (sn)" type="number" min={1} max={60} value={s.retryAfter} onChange={(e) => update(i, { retryAfter: e.target.value })} />
              )}
              <div className="flex flex-col gap-1">
                <label htmlFor={`btype-${i}`} className="text-xs">
                  Gövde
                </label>
                <select id={`btype-${i}`} value={s.bodyType} onChange={(e) => update(i, { bodyType: e.target.value as StepForm["bodyType"] })} className="min-h-9 rounded-md border border-ink-500 bg-ink-900 px-2 text-sm">
                  <option value="none">yok</option>
                  <option value="json">JSON</option>
                  <option value="text">metin</option>
                </select>
              </div>
              <Button
                type="button"
                variant="ghost"
                disabled={steps.length <= 1}
                onClick={() => {
                  setSteps((x) => x.filter((_, j) => j !== i));
                  setDirty(true);
                }}
                aria-label={`Deneme ${i + 1} adımını sil`}
              >
                Sil
              </Button>
            </div>
            {s.bodyType !== "none" && <TextArea label={`Deneme ${i + 1} gövdesi`} value={s.body} rows={2} maxLength={4096} onChange={(e) => update(i, { body: e.target.value })} />}
          </Card>
        ))}
        <div>
          <Button
            type="button"
            variant="secondary"
            disabled={steps.length >= 10}
            onClick={() => {
              setSteps((x) => [...x, { status: 200, delayMs: 0, bodyType: "json", body: "{}", retryAfter: "" }]);
              setDirty(true);
            }}
          >
            + Adım ekle
          </Button>
        </div>
      </section>

      <Card>
        <h2 className="mb-2 font-semibold">Önizleme (simülatörün karar fonksiyonu)</h2>
        {defErrors.length > 0 ? (
          <Alert tone="error" title="Tanım geçersiz">
            <ul className="list-disc pl-5">
              {defErrors.map(([k, v]) => (
                <li key={k}>
                  <code className="font-mono">{k.replace(/^definition\./, "")}</code>: {v.join(" ")}
                </li>
              ))}
            </ul>
          </Alert>
        ) : (
          <ol className="flex flex-col gap-1 text-sm" data-testid="custom-preview">
            {preview?.map((p) => (
              <li key={p.attempt} className="flex flex-wrap gap-2 font-mono">
                <span className="text-muted">#{p.attempt}</span>
                <span>{p.status}</span>
                {p.delayMs > 0 && <span className="text-muted">+{p.delayMs} ms</span>}
                {p.retryAfter && <span className="text-warn">Retry-After: {p.retryAfter}</span>}
                <span className="truncate text-fog-300">{p.body}</span>
              </li>
            ))}
          </ol>
        )}
      </Card>

      <div className="flex flex-wrap gap-2">
        <Button onClick={save} busy={busy} disabled={defErrors.length > 0}>
          {existing ? "Yeni sürüm olarak kaydet" : "Senaryoyu oluştur"}
        </Button>
        {!existing && (
          <>
            <input ref={fileRef} type="file" accept="application/json,.json" className="sr-only" id="import-file" aria-label="İçe aktarılacak senaryo JSON dosyası" tabIndex={-1} onChange={(e) => e.target.files?.[0] && importFile(e.target.files[0])} />
            <Button variant="secondary" onClick={() => fileRef.current?.click()}>
              JSON içe aktar
            </Button>
          </>
        )}
        {existing && (
          <>
            <Button variant="secondary" onClick={() => download(`/api/scenarios/custom/${existing.id}/export`, "apiarena-custom-scenario.json")}>
              JSON dışa aktar
            </Button>
            <ConfirmDialog
              title="Senaryoyu arşivle"
              confirmLabel="Arşivle"
              danger
              onConfirm={async () => {
                await api(`/api/scenarios/custom/${existing.id}`, { method: "DELETE" });
                router.push("/scenarios");
              }}
              trigger={(open) => (
                <Button variant="ghost" onClick={open}>
                  Arşivle
                </Button>
              )}
            >
              Senaryo katalogdan kalkar; mevcut koşular ve raporlar kendi sürümlerini korur.
            </ConfirmDialog>
          </>
        )}
      </div>
    </div>
  );
}
