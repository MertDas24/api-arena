"use client";

import Link from "next/link";
import { useParams } from "next/navigation";
import { RequireAuth } from "@/components/require-auth";
import { ScenarioDetails } from "@/components/scenario-details";
import { Card, ErrorState, PageHeader, Spinner } from "@/components/ui";
import type { Scenario } from "@/lib/types";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

function Detail() {
  const { slug } = useParams<{ slug: string }>();
  const { data, error, loading, reload } = useApi<Scenario>(`/api/scenarios/${encodeURIComponent(slug)}`);
  if (loading) return <Spinner label={tr.common.loading} />;
  if (error || !data) return <ErrorState error={error} onRetry={reload} />;
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={data.title} description={data.summary} actions={<Link href="/projects" className="text-sm text-accent-300 underline">{tr.projects.newRun}: {tr.projects.title} →</Link>} />
      <Card>
        <ScenarioDetails version={data.latest} />
      </Card>
    </div>
  );
}

export default function ScenarioPage() {
  return (
    <RequireAuth>
      <Detail />
    </RequireAuth>
  );
}
