export type RunStatus = "Created" | "Running" | "Completed" | "Cancelled" | "Expired" | "Failed";
export type CheckStatus = "Passed" | "Failed" | "Inconclusive" | "NotApplicable";
export type EvidenceKind = "Observed" | "AdapterReported" | "SelfReported" | "None";
export type DeliveryOutcome = "Planned" | "Sent" | "ClientDisconnected" | "Unknown" | "Rejected";

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface Project {
  id: string;
  name: string;
  description: string | null;
  captureBodies: boolean;
  allowedOrigins: string[];
  createdAt: string;
  updatedAt: string;
  runCount: number;
  activeRunCount: number;
  lastRunAt: string | null;
}

export interface ScenarioContent {
  description: string;
  learningGoal: string;
  setup: string[];
  expectedTrace: string[];
  evaluationRules: string[];
  unobservable: string[];
  limits: string[];
  curlExample: string;
  connection: string;
}

export interface ScenarioVersion {
  id: string;
  version: number;
  engine: string;
  evaluatorVersion: string;
  definition: Record<string, unknown>;
  content: ScenarioContent;
  createdAt: string;
}

export interface Scenario {
  id: string;
  slug: string;
  kind: "BuiltIn" | "Custom";
  title: string;
  summary: string;
  isOwn: boolean;
  latest: ScenarioVersion;
  versions: number[];
}

export interface RunKey {
  id: string;
  prefix: string;
  createdAt: string;
  expiresAt: string;
  revokedAt: string | null;
  active: boolean;
}

export interface CreatedRunKey {
  id: string;
  key: string;
  prefix: string;
  expiresAt: string;
  simulatorBaseUrl: string;
  revokedPrevious: number;
}

export interface Run {
  id: string;
  projectId: string;
  projectName: string;
  scenarioSlug: string;
  scenarioTitle: string;
  scenarioVersion: number;
  engine: string;
  evaluatorVersion: string;
  status: RunStatus;
  label: string | null;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  expiresAt: string | null;
  inboundCount: number;
  inboundLimit: number;
  ttlSeconds: number;
  quotaExceeded: boolean;
  incompleteReason: string | null;
  simulatorBaseUrl: string;
  method: string | null;
  route: string | null;
  simulatorUrl: string | null;
  keys: RunKey[];
  hasReport: boolean;
  lastSequence: number;
  serverTime: string;
  config: Record<string, unknown>;
  webhookTargetId?: string | null;
}

export interface RunListItem {
  id: string;
  scenarioSlug: string;
  scenarioVersion: number;
  status: RunStatus;
  label: string | null;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  inboundCount: number;
  hasReport: boolean;
  passed: number | null;
  failed: number | null;
  inconclusive: number | null;
}

export interface ObservationSummary {
  id: string;
  sequence: number;
  method: string;
  route: string;
  operationKey: string | null;
  attempt: number;
  receivedAt: string;
  plannedStatus: number;
  plannedDelayMs: number;
  outcome: DeliveryOutcome;
  durationMs: number | null;
  rejectReason: string | null;
  isSimulatedError: boolean;
}

export interface ObservationDetail {
  summary: ObservationSummary;
  headers: Record<string, string>;
  bodyBytes: number;
  bodyPreview: string | null;
  bodyTruncated: boolean;
  bodyCaptureEnabled: boolean;
  responsePreview: string | null;
}

export interface FeedItem {
  sequence: number;
  type: string;
  occurredAt: string;
  observationId: string | null;
  data: Record<string, unknown> | null;
  observation: ObservationSummary | null;
}

export interface FeedPage {
  items: FeedItem[];
  lastSequence: number;
  hasMore: boolean;
  runStatus: RunStatus;
  expiresAt: string | null;
  inboundCount: number;
  inboundLimit: number;
  serverTime: string;
}

export interface ReportCheck {
  id: string;
  title: string;
  status: CheckStatus;
  evidenceKind: EvidenceKind;
  expected: string;
  observed: string;
  explanation: string;
  suggestion: string | null;
  evidence: number[];
}

export interface TimelineEntry {
  sequence: number;
  receivedAt: string;
  offsetMs: number;
  method: string;
  route: string;
  attempt: number;
  operationKey: string | null;
  plannedStatus: number;
  outcome: DeliveryOutcome;
  durationMs: number | null;
  rejectReason: string | null;
}

export interface DeliveryEntry {
  deliveryId: string;
  eventId: string;
  eventVersion: number;
  plannedOrder: number;
  attemptNumber: number;
  sentAt: string | null;
  responseStatus: number | null;
  outcome: string;
}

export interface ReportCounts {
  passed: number;
  failed: number;
  inconclusive: number;
  notApplicable: number;
}

export interface Report {
  reportId: string;
  runId: string;
  projectId: string;
  projectName: string;
  scenarioSlug: string;
  scenarioVersion: number;
  scenarioTitle: string;
  engine: string;
  evaluatorVersion: string;
  runStatus: RunStatus;
  label: string | null;
  createdAt: string;
  startedAt: string | null;
  endedAt: string | null;
  runDurationMs: number | null;
  incompleteReason: string | null;
  incompleteReasonText: string | null;
  generatedAt: string;
  counts: ReportCounts;
  requestCount: number;
  rejectedCount: number;
  inboundLimit: number;
  checks: ReportCheck[];
  unverifiable: string[];
  timeline: TimelineEntry[];
  timelineTruncated: boolean;
  deliveries: DeliveryEntry[];
  adapterUsed: boolean;
  notice: string;
}

export interface CompareSide {
  runId: string;
  scenarioSlug: string;
  scenarioVersion: number;
  evaluatorVersion: string;
  runStatus: RunStatus;
  label: string | null;
  createdAt: string;
  counts: ReportCounts;
  requestCount: number;
  runDurationMs: number | null;
  incompleteReason: string | null;
}

export interface CompareResult {
  left: CompareSide;
  right: CompareSide;
  comparable: boolean;
  warnings: string[];
  checks: { id: string; title: string; left: CheckStatus | null; right: CheckStatus | null; change: string }[];
}

export interface DashboardRun {
  id: string;
  projectId: string;
  projectName: string;
  scenarioSlug: string;
  scenarioVersion: number;
  status: RunStatus;
  label: string | null;
  createdAt: string;
  expiresAt: string | null;
  hasReport: boolean;
  passed: number | null;
  failed: number | null;
  inconclusive: number | null;
}

export interface Dashboard {
  projectCount: number;
  activeRuns: number;
  activeRunLimit: number;
  runsStartedLast24h: number;
  dailyRunLimit: number;
  requestsPerRun: number;
  runTtlMinutes: number;
  recentRuns: DashboardRun[];
}
