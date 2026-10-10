"use client";

import { useId, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type TextareaHTMLAttributes } from "react";
import { ApiError } from "@/lib/api";
import { tr } from "@/lib/i18n/tr";

type Variant = "primary" | "secondary" | "danger" | "ghost";

const variants: Record<Variant, string> = {
  primary: "bg-accent-500 text-white hover:bg-accent-400 disabled:bg-ink-600 disabled:text-muted",
  secondary: "bg-sun text-on-sun hover:bg-sun-hover border border-sun-hover disabled:bg-ink-700 disabled:border-ink-500 disabled:text-muted",
  danger: "bg-flame text-white hover:bg-flame-hover disabled:bg-ink-600 disabled:text-muted",
  ghost: "text-fog-200 hover:bg-ink-700 disabled:text-muted",
};

export function Button({ variant = "primary", className = "", busy, children, ...rest }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; busy?: boolean }) {
  return (
    <button
      {...rest}
      disabled={rest.disabled || busy}
      aria-busy={busy || undefined}
      className={`inline-flex min-h-10 items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-semibold transition-colors disabled:cursor-not-allowed ${variants[variant]} ${className}`}
    >
      {busy && <Spinner small />}
      {children}
    </button>
  );
}

export function Spinner({ small, label }: { small?: boolean; label?: string }) {
  return (
    <span role={label ? "status" : undefined} className="inline-flex items-center gap-2">
      <span aria-hidden className={`${small ? "h-3 w-3" : "h-5 w-5"} animate-spin rounded-full border-2 border-fog-300 border-t-transparent`} />
      {label && <span className="text-sm text-muted">{label}</span>}
    </span>
  );
}

export function Field({ label, hint, error, className = "", ...rest }: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string; error?: string[] }) {
  const id = useId();
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error?.length ? `${id}-err` : undefined;
  return (
    <div className={`flex flex-col gap-1 ${className}`}>
      <label htmlFor={id} className="text-sm font-medium text-fog-200">
        {label}
      </label>
      <input
        id={id}
        aria-invalid={error?.length ? true : undefined}
        aria-describedby={[hintId, errorId].filter(Boolean).join(" ") || undefined}
        className="min-h-10 rounded-md border border-ink-500 bg-ink-900 px-3 py-2 text-fog-100 placeholder:text-muted aria-[invalid=true]:border-bad"
        {...rest}
      />
      {hint && (
        <p id={hintId} className="text-xs text-muted">
          {hint}
        </p>
      )}
      {error?.map((e, index) => (
        <p key={`${index}-${e}`} id={index === 0 ? errorId : undefined} className="text-xs text-bad">
          <span aria-hidden>⚠ </span>
          {e}
        </p>
      ))}
    </div>
  );
}

export function TextArea({ label, hint, error, className = "", ...rest }: TextareaHTMLAttributes<HTMLTextAreaElement> & { label: string; hint?: string; error?: string[] }) {
  const id = useId();
  return (
    <div className={`flex flex-col gap-1 ${className}`}>
      <label htmlFor={id} className="text-sm font-medium text-fog-200">
        {label}
      </label>
      <textarea
        id={id}
        aria-invalid={error?.length ? true : undefined}
        aria-describedby={hint || error?.length ? `${id}-d` : undefined}
        className="rounded-md border border-ink-500 bg-ink-900 px-3 py-2 font-mono text-sm text-fog-100 aria-[invalid=true]:border-bad"
        {...rest}
      />
      <div id={`${id}-d`}>
        {hint && <p className="text-xs text-muted">{hint}</p>}
        {error?.map((e, index) => (
          <p key={`${index}-${e}`} className="text-xs text-bad">
            <span aria-hidden>⚠ </span>
            {e}
          </p>
        ))}
      </div>
    </div>
  );
}

type Tone = "info" | "success" | "warning" | "error";
const tones: Record<Tone, { box: string; icon: string; label: string }> = {
  info: { box: "border-sky-accent/50 bg-sky-accent/10", icon: "ℹ", label: "Bilgi" },
  success: { box: "border-ok/50 bg-ok/10", icon: "✓", label: "Başarılı" },
  warning: { box: "border-warn/60 bg-warn/10", icon: "!", label: "Uyarı" },
  error: { box: "border-bad/60 bg-bad/10", icon: "✕", label: "Hata" },
};

export function Alert({ tone = "info", title, children }: { tone?: Tone; title?: string; children?: ReactNode }) {
  const t = tones[tone];
  return (
    <div role={tone === "error" ? "alert" : "status"} className={`flex gap-3 rounded-lg border p-3 text-sm ${t.box}`}>
      <span aria-hidden className="font-bold">
        {t.icon}
      </span>
      <div className="min-w-0">
        <span className="sr-only">{t.label}: </span>
        {title && <p className="font-semibold">{title}</p>}
        {children && <div className="text-fog-200">{children}</div>}
      </div>
    </div>
  );
}

export function Card({ children, className = "", as: Tag = "section" }: { children: ReactNode; className?: string; as?: "section" | "div" | "article" }) {
  return <Tag className={`rounded-xl border border-ink-600 bg-ink-850 p-4 shadow-sm sm:p-5 ${className}`}>{children}</Tag>;
}

export function PageHeader({ title, description, actions }: { title: string; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
      <div className="min-w-0">
        <h1 className="text-2xl font-bold tracking-tight text-fog-100 break-words">{title}</h1>
        {description && <p className="mt-1 text-sm text-muted">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </div>
  );
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center gap-3 rounded-xl border border-dashed border-ink-500 p-8 text-center">
      <p className="font-semibold">{title}</p>
      {description && <p className="max-w-md text-sm text-muted">{description}</p>}
      {action}
    </div>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const apiError = error instanceof ApiError ? error : null;
  const title = apiError?.isNotFound ? tr.common.notFound : apiError?.isUnauthenticated ? tr.common.sessionExpired : (apiError?.message ?? tr.common.errorTitle);
  return (
    <Alert tone="error" title={title}>
      {apiError && apiError.status >= 500 && <p>{tr.common.platformError}</p>}
      {apiError?.correlationId && (
        <p className="mt-1 text-xs">
          {tr.common.correlation}: <code className="font-mono">{apiError.correlationId}</code>
        </p>
      )}
      {onRetry && (
        <Button variant="secondary" className="mt-2" onClick={onRetry}>
          {tr.common.retry}
        </Button>
      )}
    </Alert>
  );
}

export function CopyButton({ value, label = tr.common.copy }: { value: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Button
      type="button"
      variant="secondary"
      className="min-h-8 px-3 py-1 text-xs"
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(value);
          setCopied(true);
          setTimeout(() => setCopied(false), 2000);
        } catch {
          setCopied(false);
        }
      }}
    >
      <span aria-live="polite">{copied ? tr.common.copied : label}</span>
    </Button>
  );
}

export function CodeBlock({ code, label }: { code: string; label: string }) {
  return (
    <figure className="min-w-0">
      <figcaption className="mb-1 flex items-center justify-between gap-2 text-xs text-muted">
        <span>{label}</span>
        <CopyButton value={code} />
      </figcaption>
      <pre tabIndex={0} className="relative overflow-x-auto rounded-md border border-ink-600 bg-ink-950 p-3 font-mono text-xs leading-relaxed text-fog-200">
        <code>{code}</code>
      </pre>
    </figure>
  );
}

export function fieldErrors(error: unknown, field: string): string[] | undefined {
  return error instanceof ApiError ? error.fieldErrors[field] : undefined;
}
