"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api, ApiError, resetCsrf } from "./api";

export interface Me {
  id: string;
  email: string;
  displayName: string | null;
  emailConfirmed: boolean;
  createdAt: string;
  runsRequireVerifiedEmail: boolean;
}

interface SessionState {
  me: Me | null;
  loading: boolean;
  refresh: () => Promise<Me | null>;
  logout: () => Promise<void>;
  setMe: (me: Me | null) => void;
}

const SessionContext = createContext<SessionState | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    try {
      const value = await api<Me>("/api/auth/me");
      setMe(value);
      return value;
    } catch (e) {
      if (e instanceof ApiError && e.isUnauthenticated) {
        setMe(null);
        return null;
      }
      throw e;
    } finally {
      setLoading(false);
    }
  }, []);

  const logout = useCallback(async () => {
    try {
      await api<void>("/api/auth/logout", { method: "POST" });
    } finally {
      resetCsrf();
      setMe(null);
    }
  }, []);

  useEffect(() => {
    let active = true;
    api<Me>("/api/auth/me")
      .then((value) => {
        if (active) setMe(value);
      })
      .catch(() => {
        if (active) setMe(null);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, []);

  const value = useMemo(() => ({ me, loading, refresh, logout, setMe }), [me, loading, refresh, logout]);
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionState {
  const ctx = useContext(SessionContext);
  if (!ctx) throw new Error("useSession must be used inside SessionProvider");
  return ctx;
}
