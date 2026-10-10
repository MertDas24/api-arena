"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { Alert, Button, Card, Field } from "@/components/ui";
import { api, ApiError, resetCsrf } from "@/lib/api";
import { useSession, type Me } from "@/lib/session";
import { safeReturnUrl } from "@/lib/url";
import { tr } from "@/lib/i18n/tr";

function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const { setMe } = useSession();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const me = await api<Me>("/api/auth/login", { method: "POST", body: { email, password } });
      resetCsrf();
      setPassword("");
      setMe(me);
      router.push(safeReturnUrl(params.get("returnUrl")));
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? tr.auth.rateLimited(err.retryAfterSeconds) : err instanceof ApiError ? err.message : tr.common.errorTitle);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="mx-auto w-full max-w-md">
      <h1 className="mb-4 text-xl font-bold">{tr.auth.loginTitle}</h1>
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        {error && <Alert tone="error">{error}</Alert>}
        <Field label={tr.auth.email} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <Field label={tr.auth.password} type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        <Button type="submit" busy={busy}>
          {tr.auth.loginSubmit}
        </Button>
      </form>
      <div className="mt-4 flex flex-col gap-1 text-sm">
        <Link className="text-accent-300 underline" href="/forgot-password">
          {tr.auth.forgot}
        </Link>
        <p className="text-muted">
          {tr.auth.noAccount}{" "}
          <Link className="text-accent-300 underline" href="/register">
            {tr.nav.register}
          </Link>
        </p>
      </div>
    </Card>
  );
}

export default function LoginPage() {
  return (
    <Suspense>
      <LoginForm />
    </Suspense>
  );
}
