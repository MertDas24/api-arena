import type { CheckStatus, DeliveryOutcome, EvidenceKind, RunStatus } from "@/lib/types";
import { tr } from "@/lib/i18n/tr";

/** Status is always conveyed by icon + text, colour is only a secondary cue (UX-04). */
const checkView: Record<CheckStatus, { icon: string; cls: string }> = {
  Passed: { icon: "✓", cls: "border-ok/60 text-ok" },
  Failed: { icon: "✕", cls: "border-bad/60 text-bad" },
  Inconclusive: { icon: "?", cls: "border-warn/60 text-warn" },
  NotApplicable: { icon: "–", cls: "border-unknown/60 text-unknown" },
};

export function CheckBadge({ status }: { status: CheckStatus }) {
  const v = checkView[status];
  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-semibold ${v.cls}`}>
      <span aria-hidden>{v.icon}</span>
      {tr.check[status]}
    </span>
  );
}

const runView: Record<RunStatus, { icon: string; cls: string }> = {
  Created: { icon: "○", cls: "border-unknown/60 text-fog-300" },
  Running: { icon: "●", cls: "border-sky-accent/70 text-sky-accent" },
  Completed: { icon: "■", cls: "border-fog-300/60 text-fog-200" },
  Cancelled: { icon: "⊘", cls: "border-warn/60 text-warn" },
  Expired: { icon: "⌛", cls: "border-warn/60 text-warn" },
  Failed: { icon: "!", cls: "border-bad/60 text-bad" },
};

export function RunStatusBadge({ status }: { status: RunStatus }) {
  const v = runView[status];
  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-semibold ${v.cls}`}>
      <span aria-hidden>{v.icon}</span>
      {tr.runStatus[status]}
    </span>
  );
}

export function EvidenceTag({ kind }: { kind: EvidenceKind }) {
  if (kind === "None") return null;
  return (
    <span className="rounded border border-ink-500 px-1.5 py-0.5 text-[11px] text-fog-300" title={tr.evidence[kind].hint}>
      {tr.evidence[kind].label}
    </span>
  );
}

const outcomeView: Record<DeliveryOutcome, string> = {
  Planned: "…",
  Sent: "→",
  ClientDisconnected: "⨯",
  Unknown: "?",
  Rejected: "⊘",
};

export function OutcomeText({ outcome }: { outcome: DeliveryOutcome }) {
  return (
    <span className="whitespace-nowrap">
      <span aria-hidden>{outcomeView[outcome]} </span>
      {tr.outcome[outcome]}
    </span>
  );
}

/** HTTP status shown with its meaning: expected simulated errors are distinguished from platform errors (UX-07). */
export function HttpStatus({ code, simulated, rejected }: { code: number; simulated: boolean; rejected?: boolean }) {
  const cls = rejected ? "text-unknown" : code >= 500 ? "text-bad" : code >= 400 ? "text-warn" : "text-ok";
  return (
    <span className={`font-mono font-semibold ${cls}`}>
      {code}
      <span className="ml-1 font-sans text-[11px] font-normal text-muted">{rejected ? tr.live.platformRejected : simulated ? tr.live.expectedSimError : ""}</span>
    </span>
  );
}
