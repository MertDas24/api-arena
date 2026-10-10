"use client";

import { useParams, useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { CustomScenarioEditor } from "@/components/custom-scenario-editor";
import { RequireAuth } from "@/components/require-auth";
import { ScenarioDetails } from "@/components/scenario-details";
import { Alert, Card, ErrorState, PageHeader, Spinner } from "@/components/ui";
import type { Scenario } from "@/lib/types";
import { useApi } from "@/lib/use-api";

function Editor() {
  const { id } = useParams<{ id: string }>();
  const params = useSearchParams();
  const { data, error, loading, reload } = useApi<Scenario>(`/api/scenarios/custom/${id}`);
  if (loading) return <Spinner label="Yükleniyor…" />;
  if (error || !data) return <ErrorState error={error} onRetry={reload} />;
  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={data.title} description={`Özel senaryo · sürümler: ${data.versions.map((v) => "v" + v).join(", ")}`} />
      {params.get("saved") && <Alert tone="success">Sürüm v{params.get("saved")} kaydedildi.</Alert>}
      <CustomScenarioEditor key={data.latest.id} existing={data} />
      <Card>
        <h2 className="mb-2 font-semibold">Son sürümün koşu talimatları</h2>
        <ScenarioDetails version={data.latest} />
      </Card>
    </div>
  );
}

export default function CustomScenarioPage() {
  return (
    <RequireAuth>
      <Suspense>
        <Editor />
      </Suspense>
    </RequireAuth>
  );
}
