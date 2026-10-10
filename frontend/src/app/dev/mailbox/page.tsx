"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { Alert, Button, Card, EmptyState, ErrorState, PageHeader, Spinner } from "@/components/ui";
import { useApi } from "@/lib/use-api";
import { tr } from "@/lib/i18n/tr";

interface StoredEmail {
  id: string;
  to: string;
  subject: string;
  textBody: string;
  category: string;
  createdAt: string;
}

/** Turns the plain-text mail body into text + same-origin links (React escapes everything; no HTML injection). */
function Body({ text }: { text: string }) {
  const parts = text.split(/(https?:\/\/\S+)/g);
  return (
    <p className="whitespace-pre-wrap break-words text-sm text-fog-200">
      {parts.map((p, i) => {
        if (!/^https?:\/\//.test(p)) return <span key={i}>{p}</span>;
        let path: string | null = null;
        try {
          const u = new URL(p);
          path = u.pathname + u.search;
        } catch {
          path = null;
        }
        return path ? (
          <a key={i} className="text-accent-300 underline" href={path}>
            {p}
          </a>
        ) : (
          <span key={i}>{p}</span>
        );
      })}
    </p>
  );
}

function Mailbox() {
  const params = useSearchParams();
  const to = params.get("to");
  const { data: mails, error, loading, reload } = useApi<StoredEmail[]>(`/api/dev/mailbox${to ? `?to=${encodeURIComponent(to)}` : ""}`);

  return (
    <>
      <PageHeader
        title={tr.auth.devMailbox}
        description={tr.auth.devMailboxNote}
        actions={
          <Button variant="secondary" onClick={reload}>
            {tr.common.retry}
          </Button>
        }
      />
      <Alert tone="warning">Bu sayfa production derlemesinde çalışmaz: endpoint yalnızca Development/Testing ortamında vardır.</Alert>
      <div className="mt-4 flex flex-col gap-3">
        {error ? <ErrorState error={error} onRetry={reload} /> : null}
        {loading && <Spinner label={tr.common.loading} />}
        {mails?.length === 0 && <EmptyState title="Henüz e-posta yok" />}
        {mails?.map((m) => (
          <Card key={m.id} as="article">
            <p className="text-xs text-muted">
              {new Date(m.createdAt).toLocaleString("tr-TR")} · {m.to} · <code className="font-mono">{m.category}</code>
            </p>
            <h2 className="my-1 font-semibold">{m.subject}</h2>
            <Body text={m.textBody} />
          </Card>
        ))}
      </div>
    </>
  );
}

export default function DevMailboxPage() {
  return (
    <Suspense>
      <Mailbox />
    </Suspense>
  );
}
