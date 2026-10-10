"use client";

import { useEffect, useRef, useState } from "react";
import { api } from "./api";
import { applyFeed, emptyFeed, type FeedState } from "./feed";
import type { FeedPage, RunStatus } from "./types";

const POLL_MS = 1500;

/**
 * afterSequence polling (ADR-009). Pausing only stops screen updates; the server keeps recording, and resuming
 * continues from the last rendered sequence so nothing is lost or duplicated. Stops polling once the run has ended.
 */
export function useLiveFeed(runId: string, paused: boolean) {
  const [state, setState] = useState<FeedState>(emptyFeed);
  const [meta, setMeta] = useState<Pick<FeedPage, "runStatus" | "expiresAt" | "inboundCount" | "inboundLimit" | "serverTime"> | null>(null);
  const [failures, setFailures] = useState(0);
  const lastSeq = useRef(0);

  useEffect(() => {
    if (paused) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let errors = 0;

    const tick = async () => {
      try {
        let more = true;
        let status: RunStatus | null = null;
        while (more && !cancelled) {
          const page = await api<FeedPage>(`/api/runs/${runId}/events?afterSequence=${lastSeq.current}&limit=200`);
          if (cancelled) return;
          setState((s) => applyFeed(s, page));
          lastSeq.current = Math.max(lastSeq.current, page.lastSequence);
          setMeta({ runStatus: page.runStatus, expiresAt: page.expiresAt, inboundCount: page.inboundCount, inboundLimit: page.inboundLimit, serverTime: page.serverTime });
          status = page.runStatus;
          more = page.hasMore;
        }
        errors = 0;
        setFailures(0);
        if (!cancelled && (status === "Running" || status === "Created")) timer = setTimeout(tick, POLL_MS);
      } catch {
        errors++;
        setFailures(errors);
        if (!cancelled) timer = setTimeout(tick, Math.min(15000, POLL_MS * 2 ** errors));
      }
    };
    void tick();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [runId, paused]);

  return { state, meta, connectionLost: failures > 1 };
}
