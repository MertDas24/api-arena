import Link from "next/link";
import type { Dashboard } from "@/lib/types";
import type { Me } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

export interface NextStep {
  text: string;
  href: string;
  cta: string;
}

/** Pure decision so it can be unit tested: what is the single most useful next action for this user? */
export function nextStepFor(me: Me, dashboard: Dashboard): NextStep {
  if (me.runsRequireVerifiedEmail && !me.emailConfirmed) {
    return { text: tr.nextStep.verify, href: "/account", cta: tr.auth.resend };
  }
  if (dashboard.projectCount === 0) {
    return { text: tr.nextStep.createProject, href: "/projects?create=1", cta: tr.projects.create };
  }
  const active = dashboard.recentRuns.find((r) => r.status === "Running" || r.status === "Created");
  if (active) {
    return { text: tr.nextStep.runActive, href: `/runs/${active.id}`, cta: tr.live.title };
  }
  const reported = dashboard.recentRuns.find((r) => r.hasReport);
  if (reported) {
    return { text: tr.nextStep.readReport, href: `/runs/${reported.id}/report`, cta: tr.live.openReport };
  }
  const project = dashboard.recentRuns[0]?.projectId;
  return { text: tr.nextStep.startRun, href: project ? `/projects/${project}/runs/new` : "/projects", cta: tr.projects.newRun };
}

export function NextStepPanel({ step }: { step: NextStep }) {
  return (
    <section aria-labelledby="next-step" className="rounded-xl border border-accent-500/50 bg-accent-500/10 p-4 sm:p-5">
      <h2 id="next-step" className="text-sm font-semibold uppercase tracking-wide text-accent-300">
        {tr.nextStep.title}
      </h2>
      <p className="mt-1 text-fog-100">{step.text}</p>
      <Link href={step.href} className="mt-3 inline-flex min-h-10 items-center rounded-md bg-accent-500 px-4 py-2 text-sm font-semibold text-white hover:bg-accent-400">
        {step.cta} →
      </Link>
    </section>
  );
}
