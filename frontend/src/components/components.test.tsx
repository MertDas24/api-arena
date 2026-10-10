import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { nextStepFor } from "./next-step";
import { ObservationDetailPanel } from "./observation-detail";
import { ReportView } from "./report-view";
import { SecretOnce } from "./secret-once";
import { CheckBadge } from "./status";
import type { Dashboard, Report } from "@/lib/types";
import type { Me } from "@/lib/session";

const me: Me = { id: "u", email: "a@b.test", displayName: null, emailConfirmed: true, createdAt: "2026-10-07T00:00:00Z", runsRequireVerifiedEmail: true };
const dash: Dashboard = { projectCount: 1, activeRuns: 0, activeRunLimit: 3, runsStartedLast24h: 0, dailyRunLimit: 30, requestsPerRun: 200, runTtlMinutes: 5, recentRuns: [] };

describe("nextStepFor (UX-02)", () => {
  it("asks unverified users to verify first", () => {
    expect(nextStepFor({ ...me, emailConfirmed: false }, dash).href).toBe("/account");
  });
  it("asks for a project when there is none", () => {
    expect(nextStepFor(me, { ...dash, projectCount: 0 }).href).toContain("/projects");
  });
  it("points to the running run, then to the latest report", () => {
    const run = { id: "r1", projectId: "p", projectName: "P", scenarioSlug: "retry-503-v1", scenarioVersion: 1, label: null, createdAt: "", expiresAt: null, passed: null, failed: null, inconclusive: null };
    expect(nextStepFor(me, { ...dash, recentRuns: [{ ...run, status: "Running", hasReport: false }] }).href).toBe("/runs/r1");
    expect(nextStepFor(me, { ...dash, recentRuns: [{ ...run, status: "Completed", hasReport: true }] }).href).toBe("/runs/r1/report");
  });
});

describe("XSS: recorded traffic is rendered as plain text (SEC-04)", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("never turns a hostile body preview or header into markup", async () => {
    const payload = '<script>window.__pwned=1</script><img src=x onerror="window.__pwned=2">';
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        new Response(
          JSON.stringify({
            summary: { id: "o", sequence: 1, method: "POST", route: "<b>x</b>", operationKey: "<i>k</i>", attempt: 1, receivedAt: "2026-10-07T00:00:00Z", plannedStatus: 503, plannedDelayMs: 0, outcome: "Sent", durationMs: 3, rejectReason: null, isSimulatedError: true },
            headers: { "user-agent": payload, authorization: "[REDACTED]" },
            bodyBytes: 10,
            bodyPreview: payload,
            bodyTruncated: false,
            bodyCaptureEnabled: true,
            responsePreview: payload,
          }),
          { headers: { "content-type": "application/json" } },
        ),
      ),
    );
    const { container } = render(<ObservationDetailPanel runId="r" observationId="o" />);
    expect(await screen.findByTestId("body-preview")).toHaveTextContent("<script>window.__pwned=1</script>");
    expect(container.querySelector("script")).toBeNull();
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector("b")).toBeNull();
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
  });

  it("report text fields are escaped too", () => {
    const report = {
      reportId: "x", runId: "r", projectId: "p", projectName: "<img src=x onerror=alert(1)>", scenarioSlug: "retry-503-v1", scenarioVersion: 1, scenarioTitle: "t", engine: "retry-503",
      evaluatorVersion: "retry-503@1.0", runStatus: "Completed", label: null, createdAt: "", startedAt: null, endedAt: null, runDurationMs: null, incompleteReason: null,
      incompleteReasonText: null, generatedAt: "2026-10-07T00:00:00Z", counts: { passed: 0, failed: 0, inconclusive: 1, notApplicable: 0 }, requestCount: 1, rejectedCount: 0, inboundLimit: 200,
      checks: [{ id: "c", title: "<script>x</script>", status: "Inconclusive", evidenceKind: "None", expected: "e", observed: "<img src=x>", explanation: "x", suggestion: null, evidence: [] }],
      unverifiable: ["<b>u</b>"], timeline: [], timelineTruncated: false, deliveries: [], adapterUsed: false, notice: "n",
    } as Report;
    const { container } = render(<ReportView report={report} />);
    expect(container.querySelector("script, img, b")).toBeNull();
    expect(screen.getByText("<script>x</script>")).toBeInTheDocument();
  });
});

describe("SecretOnce (UX-06)", () => {
  it("is masked by default and only revealed on request", async () => {
    const secret = "aa_rk_0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";
    render(<SecretOnce value={secret} label="Run key" />);
    expect(screen.getByTestId("secret-value")).not.toHaveTextContent(secret);
    await userEvent.click(screen.getByRole("button", { name: "Anahtarı göster" }));
    expect(screen.getByTestId("secret-value")).toHaveTextContent(secret);
  });
});

describe("status is not conveyed by colour alone (UX-04)", () => {
  it.each(["Passed", "Failed", "Inconclusive", "NotApplicable"] as const)("%s has a text label", (s) => {
    render(<CheckBadge status={s} />);
    expect(screen.getByText(s)).toBeInTheDocument();
  });
});
