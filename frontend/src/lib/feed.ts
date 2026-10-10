import type { FeedItem, FeedPage, ObservationSummary } from "./types";

export const MAX_BUFFER = 500;

export interface FeedState {
  lastSequence: number;
  /** Most recent first, bounded to MAX_BUFFER entries (no unbounded memory growth). */
  observations: ObservationSummary[];
  lifecycle: FeedItem[];
}

export const emptyFeed: FeedState = { lastSequence: 0, observations: [], lifecycle: [] };

/**
 * Applies one page of the append-only feed. Items at or below lastSequence are ignored, so a replayed or overlapping
 * page after reconnect never duplicates rows; ResponseOutcome events update the matching observation in place.
 */
export function applyFeed(state: FeedState, page: Pick<FeedPage, "items">): FeedState {
  const fresh = page.items.filter((i) => i.sequence > state.lastSequence).sort((a, b) => a.sequence - b.sequence);
  if (fresh.length === 0) return state;

  const byId = new Map(state.observations.map((o) => [o.id, o] as const));
  const order = state.observations.map((o) => o.id);
  const lifecycle = [...state.lifecycle];

  for (const item of fresh) {
    if ((item.type === "RequestReceived" || item.type === "RequestRejected") && item.observation) {
      if (!byId.has(item.observation.id)) order.unshift(item.observation.id);
      byId.set(item.observation.id, item.observation);
    } else if (item.type === "ResponseOutcome" && item.observationId) {
      const existing = byId.get(item.observationId);
      const data = item.data as { outcome?: ObservationSummary["outcome"]; durationMs?: number } | null;
      if (existing && data?.outcome) {
        byId.set(existing.id, { ...existing, outcome: data.outcome, durationMs: data.durationMs ?? existing.durationMs });
      }
    } else {
      lifecycle.push(item);
    }
  }

  const ids = order.slice(0, MAX_BUFFER);
  return {
    lastSequence: fresh[fresh.length - 1].sequence,
    observations: ids.map((id) => byId.get(id)!).filter(Boolean),
    lifecycle: lifecycle.slice(-50),
  };
}

export type TrafficFilter = "all" | "simError" | "ok" | "rejected";

export function filterTraffic(items: ObservationSummary[], filter: TrafficFilter): ObservationSummary[] {
  switch (filter) {
    case "simError":
      return items.filter((o) => o.isSimulatedError);
    case "ok":
      return items.filter((o) => o.outcome !== "Rejected" && o.plannedStatus < 400);
    case "rejected":
      return items.filter((o) => o.outcome === "Rejected");
    default:
      return items;
  }
}
