import { CodeBlock } from "./ui";
import type { ScenarioVersion } from "@/lib/types";
import { tr } from "@/lib/i18n/tr";

function List({ title, items, ordered }: { title: string; items: string[]; ordered?: boolean }) {
  const Tag = ordered ? "ol" : "ul";
  return (
    <div>
      <h3 className="mb-1 text-sm font-semibold text-fog-200">{title}</h3>
      <Tag className={`${ordered ? "list-decimal" : "list-disc"} space-y-1 pl-5 text-sm text-fog-300`}>
        {items.map((item, index) => (
          // Items may repeat (e.g. two "bekleme" steps in an expected trace), so the position is part of the key.
          <li key={`${index}-${item}`}>{item}</li>
        ))}
      </Tag>
    </div>
  );
}

/** All teaching content of a scenario version (C6), rendered as plain text. */
export function ScenarioDetails({ version, simBaseUrl }: { version: ScenarioVersion; simBaseUrl?: string }) {
  const c = version.content;
  const curl = simBaseUrl ? c.curlExample.replaceAll("$SIM_BASE_URL", simBaseUrl) : c.curlExample;
  return (
    <div className="flex flex-col gap-4">
      <p className="text-fog-200">{c.description}</p>
      <div className="rounded-lg border border-accent-500/40 bg-accent-500/10 p-3 text-sm">
        <span className="font-semibold">{tr.scenarios.learningGoal}: </span>
        {c.learningGoal}
      </div>
      <div className="grid gap-4 md:grid-cols-2">
        <List title={tr.scenarios.setup} items={c.setup} ordered />
        <List title={tr.scenarios.expectedTrace} items={c.expectedTrace} ordered />
        <List title={tr.scenarios.rules} items={c.evaluationRules} />
        <List title={tr.scenarios.unobservable} items={c.unobservable} />
        <List title={tr.scenarios.limits} items={c.limits} />
        <div>
          <h3 className="mb-1 text-sm font-semibold text-fog-200">{tr.scenarios.connection}</h3>
          <p className="text-sm text-fog-300">{c.connection}</p>
        </div>
      </div>
      <CodeBlock label={tr.scenarios.curl} code={curl} />
      <p className="text-xs text-muted">
        {tr.scenarios.version} {version.version} · <code className="font-mono">{version.engine}</code> · <code className="font-mono">{version.evaluatorVersion}</code>
      </p>
    </div>
  );
}
