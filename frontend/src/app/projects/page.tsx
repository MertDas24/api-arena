"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { RequireAuth } from "@/components/require-auth";
import { Alert, Button, Card, EmptyState, ErrorState, Field, PageHeader, Spinner, TextArea, fieldErrors } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import type { Paged, Project } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function CreateProject({ onCreated }: { onCreated: (p: Project) => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onCreated(await api<Project>("/api/projects", { method: "POST", body: { name, description: description || null } }));
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  const general = error instanceof ApiError && Object.keys(error.fieldErrors).length === 0 ? error.message : null;
  return (
    <Card>
      <form onSubmit={submit} className="flex flex-col gap-3" noValidate>
        <h2 className="font-semibold">{tr.projects.create}</h2>
        {general && <Alert tone="error">{general}</Alert>}
        <Field label={tr.projects.name} value={name} maxLength={80} required onChange={(e) => setName(e.target.value)} error={fieldErrors(error, "name")} />
        <TextArea label={`${tr.projects.description} (${tr.common.optional})`} value={description} maxLength={500} rows={2} onChange={(e) => setDescription(e.target.value)} error={fieldErrors(error, "description")} />
        <div>
          <Button type="submit" busy={busy}>
            {tr.projects.create}
          </Button>
        </div>
      </form>
    </Card>
  );
}

function ProjectList() {
  const router = useRouter();
  const params = useSearchParams();
  const [page, setPage] = useState(1);
  const [showCreate, setShowCreate] = useState(params.get("create") === "1");
  const { data, error, loading, reload } = useApi<Paged<Project>>(`/api/projects?page=${page}&pageSize=10&sort=createdAt&order=desc`);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={tr.projects.title}
        actions={
          <Button onClick={() => setShowCreate((v) => !v)} aria-expanded={showCreate}>
            {tr.projects.create}
          </Button>
        }
      />
      {showCreate && <CreateProject onCreated={(p) => router.push(`/projects/${p.id}`)} />}
      {loading && <Spinner label={tr.common.loading} />}
      {error ? <ErrorState error={error} onRetry={reload} /> : null}
      {data && data.total === 0 && !showCreate && (
        <EmptyState title={tr.projects.empty} description={tr.projects.emptyHint} action={<Button onClick={() => setShowCreate(true)}>{tr.projects.create}</Button>} />
      )}
      {data && data.items.length > 0 && (
        <ul className="grid gap-3 md:grid-cols-2">
          {data.items.map((p) => (
            <li key={p.id}>
              <Link href={`/projects/${p.id}`} className="block h-full rounded-xl border border-ink-600 bg-ink-850 p-4 hover:border-accent-500">
                <h2 className="truncate font-semibold">{p.name}</h2>
                {p.description && <p className="mt-1 line-clamp-2 text-sm text-muted">{p.description}</p>}
                <p className="mt-2 text-xs text-muted">
                  {p.runCount} koşu · {p.activeRunCount} aktif · son: {formatDateTime(p.lastRunAt)}
                </p>
              </Link>
            </li>
          ))}
        </ul>
      )}
      {data && pages > 1 && (
        <nav aria-label="Sayfalar" className="flex items-center justify-center gap-3">
          <Button variant="secondary" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            {tr.common.previous}
          </Button>
          <span className="text-sm">
            {tr.common.page} {page} / {pages}
          </span>
          <Button variant="secondary" disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
            {tr.common.next}
          </Button>
        </nav>
      )}
    </div>
  );
}

export default function ProjectsPage() {
  return (
    <RequireAuth>
      <Suspense>
        <ProjectList />
      </Suspense>
    </RequireAuth>
  );
}
