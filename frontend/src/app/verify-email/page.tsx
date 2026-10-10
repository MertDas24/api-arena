"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import { Alert, Button, Card, Spinner } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { useSession } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

// "limited": rate-limited (429) — the token was not consumed, so the link is still usable and must not be called invalid.
type Result = "ok" | "already" | "invalid" | "limited";

function Verify() {
  const params = useSearchParams();
  const { refresh, me } = useSession();
  const userId = params.get("uid");
  const token = params.get("token");
  const [result, setResult] = useState<Result | null>(null);
  const [limitMessage, setLimitMessage] = useState("");
  const [attempt, setAttempt] = useState(0);
  const started = useRef(-1);

  useEffect(() => {
    if (started.current === attempt || !userId || !token) return;
    started.current = attempt;
    // The token is POSTed in the body; the page sets Referrer-Policy: no-referrer so it does not leak onward.
    api<{ alreadyVerified: boolean }>("/api/auth/verify-email", { method: "POST", body: { userId, token } })
      .then((r) => {
        setResult(r.alreadyVerified ? "already" : "ok");
        void refresh().catch(() => undefined);
      })
      .catch((e: unknown) => {
        if (e instanceof ApiError && e.status === 429) {
          setLimitMessage(e.message);
          setResult("limited");
        } else {
          setResult("invalid");
        }
      });
  }, [userId, token, refresh, attempt]);

  const state: Result | "working" = !userId || !token ? "invalid" : (result ?? "working");

  return (
    <Card className="mx-auto w-full max-w-md">
      <h1 className="mb-4 text-xl font-bold">{tr.auth.verifyTitle}</h1>
      {state === "working" && <Spinner label={tr.auth.verifying} />}
      {state === "ok" && <Alert tone="success">{tr.auth.verified}</Alert>}
      {state === "already" && <Alert tone="info">{tr.auth.alreadyVerified}</Alert>}
      {state === "invalid" && <Alert tone="error">{tr.auth.linkInvalid}</Alert>}
      {state === "limited" && (
        <Alert tone="warning" title={limitMessage}>
          Bağlantınız hâlâ geçerli; biraz bekleyip tekrar deneyin.
          <Button
            variant="secondary"
            className="mt-2"
            onClick={() => {
              setResult(null);
              setAttempt((a) => a + 1);
            }}
          >
            {tr.common.retry}
          </Button>
        </Alert>
      )}
      {state !== "working" && state !== "limited" && (
        <Link className="mt-4 inline-block text-accent-300 underline" href={me ? "/dashboard" : "/login"}>
          {me ? tr.nav.dashboard : tr.auth.goLogin}
        </Link>
      )}
    </Card>
  );
}

export default function VerifyEmailPage() {
  return (
    <Suspense>
      <Verify />
    </Suspense>
  );
}
