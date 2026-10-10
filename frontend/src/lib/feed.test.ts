import { describe, expect, it } from "vitest";
import { applyFeed, emptyFeed, filterTraffic, MAX_BUFFER } from "./feed";
import type { FeedItem, ObservationSummary } from "./types";

function obs(seq: number, overrides: Partial<ObservationSummary> = {}): ObservationSummary {
  return {
    id: `o-${seq}`,
    sequence: seq,
    method: "POST",
    route: "payment/charge",
    operationKey: "k",
    attempt: 1,
    receivedAt: new Date(0).toISOString(),
    plannedStatus: 503,
    plannedDelayMs: 0,
    outcome: "Planned",
    durationMs: null,
    rejectReason: null,
    isSimulatedError: true,
    ...overrides,
  };
}

const received = (seq: number, o = obs(seq)): FeedItem => ({ sequence: seq, type: "RequestReceived", occurredAt: "", observationId: o.id, data: null, observation: o });
const outcome = (seq: number, id: string): FeedItem => ({ sequence: seq, type: "ResponseOutcome", occurredAt: "", observationId: id, data: { outcome: "Sent", durationMs: 12 }, observation: null });

describe("applyFeed (RUN-07)", () => {
  it("adds requests newest-first and applies outcome updates in place", () => {
    const s = applyFeed(emptyFeed, { items: [received(4), outcome(5, "o-4"), received(6)] });
    expect(s.observations.map((o) => o.id)).toEqual(["o-6", "o-4"]);
    expect(s.observations[1].outcome).toBe("Sent");
    expect(s.observations[1].durationMs).toBe(12);
    expect(s.lastSequence).toBe(6);
  });

  it("does not duplicate rows when a reconnect replays overlapping pages", () => {
    const first = applyFeed(emptyFeed, { items: [received(1), received(2)] });
    const replay = applyFeed(first, { items: [received(1), received(2), received(3)] });
    expect(replay.observations.map((o) => o.sequence)).toEqual([3, 2, 1]);
    expect(applyFeed(replay, { items: [received(2)] })).toBe(replay);
  });

  it("keeps a bounded buffer", () => {
    const items = Array.from({ length: MAX_BUFFER + 50 }, (_, i) => received(i + 1));
    const s = applyFeed(emptyFeed, { items });
    expect(s.observations).toHaveLength(MAX_BUFFER);
    expect(s.observations[0].sequence).toBe(MAX_BUFFER + 50);
  });

  it("filters simulated errors separately from platform rejections", () => {
    const list = [obs(1), obs(2, { plannedStatus: 200, isSimulatedError: false, outcome: "Sent" }), obs(3, { plannedStatus: 404, isSimulatedError: false, outcome: "Rejected" })];
    expect(filterTraffic(list, "simError").map((o) => o.sequence)).toEqual([1]);
    expect(filterTraffic(list, "ok").map((o) => o.sequence)).toEqual([2]);
    expect(filterTraffic(list, "rejected").map((o) => o.sequence)).toEqual([3]);
  });
});
