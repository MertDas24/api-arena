"use client";

import { useCallback, useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Button } from "./ui";
import { tr } from "@/lib/i18n/tr";

/**
 * Native modal <dialog>: focus is moved inside and trapped by the browser, Escape closes it, focus returns afterwards.
 * Destructive actions can require typing a confirmation phrase.
 */
export function ConfirmDialog({
  trigger,
  title,
  children,
  confirmLabel,
  danger,
  requirePhrase,
  onConfirm,
}: {
  trigger: (open: () => void) => ReactNode;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  danger?: boolean;
  requirePhrase?: string;
  onConfirm: () => Promise<void> | void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const [phrase, setPhrase] = useState("");
  const [busy, setBusy] = useState(false);
  const titleId = useId();
  const inputId = useId();

  const [isOpen, setIsOpen] = useState(false);
  useEffect(() => {
    const d = ref.current;
    if (isOpen && d && !d.open) d.showModal();
    const onClose = () => {
      setPhrase("");
      setIsOpen(false);
    };
    d?.addEventListener("close", onClose);
    return () => d?.removeEventListener("close", onClose);
  }, [isOpen]);

  const open = useCallback(() => setIsOpen(true), []);
  const ok = !requirePhrase || phrase === requirePhrase;
  return (
    <>
      {trigger(open)}
      <dialog ref={ref} aria-labelledby={titleId} className="m-auto w-[min(32rem,calc(100%-2rem))] rounded-xl border border-ink-500 bg-ink-850 p-0 text-fog-100 shadow-xl backdrop:bg-black/50">
        <form
          method="dialog"
          className="flex flex-col gap-4 p-5"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!ok) return;
            setBusy(true);
            try {
              await onConfirm();
              ref.current?.close();
            } finally {
              setBusy(false);
            }
          }}
        >
          <h2 id={titleId} className="text-lg font-bold">
            {title}
          </h2>
          <div className="text-sm text-fog-200">{children}</div>
          {requirePhrase && (
            <div className="flex flex-col gap-1">
              <label htmlFor={inputId} className="text-sm">
                <code className="font-mono">{requirePhrase}</code>
              </label>
              <input id={inputId} value={phrase} onChange={(e) => setPhrase(e.target.value)} autoComplete="off" className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-3" />
            </div>
          )}
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => ref.current?.close()}>
              {tr.common.cancel}
            </Button>
            <Button type="submit" variant={danger ? "danger" : "primary"} disabled={!ok} busy={busy}>
              {confirmLabel}
            </Button>
          </div>
        </form>
      </dialog>
    </>
  );
}
