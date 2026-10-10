"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "./api";

interface State<T> {
  key: string | null;
  nonce: number;
  data?: T;
  error?: unknown;
}

/**
 * Loads JSON from the control API. State is only set from promise callbacks (never synchronously in the effect),
 * stale responses for an old path are ignored, and requests are aborted on unmount/path change.
 */
export function useApi<T>(path: string | null) {
  const [nonce, setNonce] = useState(0);
  const [state, setState] = useState<State<T>>({ key: null, nonce: -1 });

  useEffect(() => {
    if (!path) return;
    const controller = new AbortController();
    api<T>(path, { signal: controller.signal })
      .then((data) => setState({ key: path, nonce, data }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ key: path, nonce, error });
      });
    return () => controller.abort();
  }, [path, nonce]);

  const reload = useCallback(() => setNonce((n) => n + 1), []);
  const setData = useCallback((data: T) => setState((s) => ({ ...s, data, error: undefined })), []);

  const current = state.key === path;
  return {
    data: current ? state.data : undefined,
    error: current ? state.error : undefined,
    /** First load for this path (no data or error yet). */
    loading: !!path && !current,
    /** A reload is in flight while older data is still shown. */
    refreshing: current && state.nonce !== nonce,
    reload,
    setData,
  };
}
