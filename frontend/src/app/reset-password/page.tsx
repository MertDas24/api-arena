"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { Alert, Button, Card, Field, fieldErrors } from "@/components/ui";
import { api, ApiError, resetCsrf } from "@/lib/api";
import { useSession } from "@/lib/session";
import { tr } from "@/lib/i18n/tr";

function ResetForm() {
  const params = useSearchParams();
  const { setMe } = useSession();
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState(false);
  const userId = params.get("uid");
  const token = params.get("token");

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api("/api/auth/reset-password", { method: "POST", body: { userId, token, newPassword: password } });
      setPassword("");
      resetCsrf();
      setMe(null);
      setDone(true);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  if (!userId || !token) return <Alert tone="error">{tr.auth.linkInvalid}</Alert>;
  if (done)
    return (
      <>
        <Alert tone="success">{tr.auth.resetDone}</Alert>
        <Link className="mt-4 inline-block text-accent-300 underline" href="/login">
          {tr.auth.goLogin}
        </Link>
      </>
    );

  const general = error instanceof ApiError && !error.fieldErrors.newPassword ? error : null;
  return (
    <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
      {general && <Alert tone="error">{general.message}</Alert>}
      <Field
        label={tr.auth.newPassword}
        type="password"
        autoComplete="new-password"
        required
        minLength={10}
        hint={tr.auth.passwordHint}
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        error={fieldErrors(error, "newPassword")}
      />
      <Button type="submit" busy={busy}>
        {tr.auth.resetSubmit}
      </Button>
    </form>
  );
}

export default function ResetPasswordPage() {
  return (
    <Card className="mx-auto w-full max-w-md">
      <h1 className="mb-4 text-xl font-bold">{tr.auth.resetTitle}</h1>
      <Suspense>
        <ResetForm />
      </Suspense>
    </Card>
  );
}
