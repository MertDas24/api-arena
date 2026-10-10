"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { DevMailboxHint } from "@/components/dev-mailbox-hint";
import { Alert, Button, Card, Field } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { tr } from "@/lib/i18n/tr";

export default function ForgotPasswordPage() {
  const [email, setEmail] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const r = await api<{ message: string }>("/api/auth/forgot-password", { method: "POST", body: { email } });
      setMessage(r.message);
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? tr.auth.rateLimited(err.retryAfterSeconds) : err instanceof ApiError ? err.message : tr.common.errorTitle);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="mx-auto w-full max-w-md">
      <h1 className="mb-4 text-xl font-bold">{tr.auth.forgotTitle}</h1>
      {message ? (
        <>
          <Alert tone="info">{message}</Alert>
          <DevMailboxHint email={email} />
        </>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
          {error && <Alert tone="error">{error}</Alert>}
          <Field label={tr.auth.email} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          <Button type="submit" busy={busy}>
            {tr.auth.forgotSubmit}
          </Button>
        </form>
      )}
      <Link className="mt-4 inline-block text-sm text-accent-300 underline" href="/login">
        {tr.auth.goLogin}
      </Link>
    </Card>
  );
}
