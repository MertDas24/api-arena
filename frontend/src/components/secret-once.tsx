"use client";

import { useState } from "react";
import { Alert, Button, CopyButton } from "./ui";
import { tr } from "@/lib/i18n/tr";

/**
 * Shows a freshly issued secret exactly once (UX-06): masked by default, explicit reveal, copy button.
 * The value lives only in component memory; it is never written to storage or the URL.
 */
export function SecretOnce({ value, label }: { value: string; label: string }) {
  const [visible, setVisible] = useState(false);
  const masked = value.slice(0, 10) + "•".repeat(Math.max(8, value.length - 10));
  return (
    <div className="flex flex-col gap-2">
      <Alert tone="warning">{tr.wizard.keyOnce}</Alert>
      <div className="flex flex-col gap-2 rounded-md border border-ink-500 bg-ink-950 p-3 sm:flex-row sm:items-center">
        <span className="sr-only">{label}</span>
        <code className="min-w-0 flex-1 break-all font-mono text-sm" data-testid="secret-value" aria-live="polite">
          {visible ? value : masked}
        </code>
        <div className="flex gap-2">
          <Button type="button" variant="secondary" className="min-h-8 px-3 py-1 text-xs" onClick={() => setVisible((v) => !v)} aria-pressed={visible}>
            {visible ? tr.wizard.hide : tr.wizard.reveal}
          </Button>
          <CopyButton value={value} />
        </div>
      </div>
    </div>
  );
}
