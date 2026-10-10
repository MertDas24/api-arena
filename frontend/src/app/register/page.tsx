"use client";

import Link from "next/link";
import { useState, type FormEvent } from "react";
import { DevMailboxHint } from "@/components/dev-mailbox-hint";
import { Alert, Button, Card, Field, fieldErrors } from "@/components/ui";
import { api, ApiError } from "@/lib/api";
import { tr } from "@/lib/i18n/tr";

export default function RegisterPage() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api("/api/auth/register", { method: "POST", body: { email, password, displayName: displayName || null } });
      setPassword("");
      setDone(true);
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  if (done) {
    return (
      <Card className="mx-auto w-full max-w-md">
        <h1 className="mb-4 text-xl font-bold">{tr.auth.registerTitle}</h1>
        <Alert tone="success">{tr.auth.registerDone}</Alert>
        <DevMailboxHint email={email} />
        <Link className="mt-4 inline-block text-accent-300 underline" href="/login">
          {tr.auth.goLogin}
        </Link>
      </Card>
    );
  }

  const general = error instanceof ApiError && Object.keys(error.fieldErrors).length === 0 ? error : null;
  return (
    <Card className="mx-auto w-full max-w-md">
      <h1 className="mb-4 text-xl font-bold">{tr.auth.registerTitle}</h1>
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        {general && <Alert tone="error">{general.status === 429 ? tr.auth.rateLimited(general.retryAfterSeconds) : general.message}</Alert>}
        <Field label={tr.auth.email} type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} error={fieldErrors(error, "email")} />
        <Field
          label={`${tr.auth.displayName} (${tr.common.optional})`}
          autoComplete="nickname"
          maxLength={80}
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          error={fieldErrors(error, "displayName")}
        />
        <Field
          label={tr.auth.password}
          type="password"
          autoComplete="new-password"
          required
          minLength={10}
          hint={tr.auth.passwordHint}
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          error={fieldErrors(error, "password")}
        />
        <Button type="submit" busy={busy}>
          {tr.auth.registerSubmit}
        </Button>
      </form>
      <p className="mt-4 text-sm text-muted">
        {tr.auth.haveAccount}{" "}
        <Link className="text-accent-300 underline" href="/login">
          {tr.nav.login}
        </Link>
      </p>
    </Card>
  );
}
