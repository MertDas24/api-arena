"use client";

import { CustomScenarioEditor } from "@/components/custom-scenario-editor";
import { RequireAuth } from "@/components/require-auth";
import { PageHeader } from "@/components/ui";

export default function NewCustomScenarioPage() {
  return (
    <RequireAuth>
      <PageHeader title="Yeni özel senaryo" description="Deklaratif, sürümlü ve yalnızca sana görünür." />
      <CustomScenarioEditor />
    </RequireAuth>
  );
}
