"use client";

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { ConfirmDialog } from "@/components/confirm-dialog";
import { RequireAuth } from "@/components/require-auth";
import { RunStatusBadge } from "@/components/status";
import { Alert, Button, Card, EmptyState, ErrorState, Field, PageHeader, Spinner, TextArea, fieldErrors } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { Paged, Project, RunListItem } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { useUnsavedChangesWarning } from "@/lib/use-unsaved";
import { tr } from "@/lib/i18n/tr";

function Settings({ project, onSaved }: { project: Project; onSaved: (p: Project) => void }) {
  const [name, setName] = useState(project.name);
  const [description, setDescription] = useState(project.description ?? "");
  const [capture, setCapture] = useState(project.captureBodies);
  const [origins, setOrigins] = useState(project.allowedOrigins.join("\n"));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const dirty = name !== project.name || description !== (project.description ?? "") || capture !== project.captureBodies || origins !== project.allowedOrigins.join("\n");
  useUnsavedChangesWarning(dirty);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const updated = await api<Project>(`/api/projects/${project.id}`, {
        method: "PATCH",
        body: { name, description, captureBodies: capture, allowedOrigins: origins.split("\n").map((o) => o.trim()).filter(Boolean) },
      });
      onSaved(updated);
      setSaved(true);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  const general = error instanceof ApiError && Object.keys(error.fieldErrors).length === 0 ? error.message : null;
  return (
    <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
      {general && <Alert tone="error">{general}</Alert>}
      {saved && !dirty && <Alert tone="success">{tr.projects.saved}</Alert>}
      {dirty && <Alert tone="warning">{tr.common.unsavedChanges}</Alert>}
      <Field label={tr.projects.name} value={name} maxLength={80} onChange={(e) => setName(e.target.value)} error={fieldErrors(error, "name")} />
      <TextArea label={tr.projects.description} value={description} maxLength={500} rows={2} onChange={(e) => setDescription(e.target.value)} error={fieldErrors(error, "description")} />
      <div className="flex items-start gap-3">
        <input id="capture" type="checkbox" className="mt-1 h-5 w-5 accent-accent-500" checked={capture} onChange={(e) => setCapture(e.target.checked)} aria-describedby="capture-hint" />
        <div>
          <label htmlFor="capture" className="text-sm font-medium">
            {tr.projects.captureBodies}
          </label>
          <p id="capture-hint" className="text-xs text-muted">
            {tr.projects.captureBodiesHint}
          </p>
        </div>
      </div>
      <TextArea label={tr.projects.origins} hint={tr.projects.originsHint} value={origins} rows={3} onChange={(e) => setOrigins(e.target.value)} error={fieldErrors(error, "allowedOrigins")} />
      <div>
        <Button type="submit" busy={busy} disabled={!dirty}>
          {tr.common.save}
        </Button>
      </div>
    </form>
  );
}

function ProjectView() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const project = useApi<Project>(`/api/projects/${id}`);
  const runs = useApi<Paged<RunListItem>>(`/api/projects/${id}/runs?pageSize=50`);

  if (project.loading) return <Spinner label={tr.common.loading} />;
  if (project.error || !project.data) return <ErrorState error={project.error} onRetry={project.reload} />;
  const p = project.data;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={p.name}
        description={p.description ?? undefined}
        actions={
          <>
            <Link href={`/projects/${p.id}/webhooks`} className="inline-flex min-h-10 items-center rounded-md border border-ink-500 px-4 py-2 text-sm font-semibold hover:bg-ink-800">
              Webhook hedefleri
            </Link>
            <Link href={`/projects/${p.id}/runs/new`} className="inline-flex min-h-10 items-center rounded-md bg-accent-500 px-4 py-2 text-sm font-semibold text-white hover:bg-accent-400">
              {tr.projects.newRun}
            </Link>
          </>
        }
      />

      <section aria-labelledby="runs">
        <h2 id="runs" className="mb-3 text-lg font-bold">
          {tr.projects.runs}
        </h2>
        {runs.loading && <Spinner label={tr.common.loading} />}
        {runs.error ? <ErrorState error={runs.error} onRetry={runs.reload} /> : null}
        {runs.data && runs.data.total === 0 && (
          <EmptyState
            title={tr.dashboard.noRuns}
            description={tr.nextStep.startRun}
            action={
              <Link href={`/projects/${p.id}/runs/new`} className="text-accent-300 underline">
                {tr.projects.newRun}
              </Link>
            }
          />
        )}
        {runs.data && runs.data.items.length > 0 && (
          <div tabIndex={0} className="relative overflow-x-auto rounded-lg border border-ink-600">
            <table className="w-full min-w-[640px] text-sm">
              <caption className="sr-only">{tr.projects.runs}</caption>
              <thead className="bg-ink-800 text-left text-xs uppercase text-muted">
                <tr>
                  <th scope="col" className="p-2">Senaryo</th>
                  <th scope="col" className="p-2">Durum</th>
                  <th scope="col" className="p-2">İstek</th>
                  <th scope="col" className="p-2">Sonuç</th>
                  <th scope="col" className="p-2">Oluşturma</th>
                  <th scope="col" className="p-2"><span className="sr-only">Bağlantı</span></th>
                </tr>
              </thead>
              <tbody>
                {runs.data.items.map((r) => (
                  <tr key={r.id} className="border-t border-ink-700">
                    <td className="p-2">
                      <code className="font-mono">{r.scenarioSlug}</code> <span className="text-muted">v{r.scenarioVersion}</span>
                      {r.label && <div className="text-xs text-muted">{r.label}</div>}
                    </td>
                    <td className="p-2"><RunStatusBadge status={r.status} /></td>
                    <td className="p-2">{r.inboundCount}</td>
                    <td className="p-2 text-xs">{r.hasReport ? `${r.passed} ✓ · ${r.failed} ✕ · ${r.inconclusive} ?` : "—"}</td>
                    <td className="p-2 text-xs text-muted">{formatDateTime(r.createdAt)}</td>
                    <td className="p-2">
                      <Link className="text-accent-300 underline" href={r.hasReport ? `/runs/${r.id}/report` : `/runs/${r.id}`}>
                        {r.hasReport ? tr.live.openReport : tr.live.title}
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <Card>
        <h2 className="mb-3 text-lg font-bold">{tr.projects.settings}</h2>
        <Settings key={p.updatedAt} project={p} onSaved={project.setData} />
      </Card>

      <Card className="border-bad/40">
        <h2 className="mb-2 text-lg font-bold">{tr.projects.delete}</h2>
        <p className="mb-3 text-sm text-muted">{tr.projects.deleteExplain}</p>
        <ConfirmDialog
          title={tr.projects.delete}
          confirmLabel={tr.projects.delete}
          danger
          requirePhrase={p.name}
          onConfirm={async () => {
            await api(`/api/projects/${p.id}`, { method: "DELETE" });
            router.push("/projects");
          }}
          trigger={(open) => (
            <Button variant="danger" onClick={open}>
              {tr.projects.delete}
            </Button>
          )}
        >
          <p>{tr.projects.deleteExplain}</p>
          <p className="mt-2">{tr.projects.deleteConfirm}:</p>
        </ConfirmDialog>
      </Card>
    </div>
  );
}

export default function ProjectPage() {
  return (
    <RequireAuth>
      <ProjectView />
    </RequireAuth>
  );
}
