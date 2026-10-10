/**
 * Same-origin client for the control API (proxied by Next.js at /api).
 * - Session lives in an HttpOnly cookie; nothing is written to localStorage/sessionStorage.
 * - The CSRF request token is kept in memory only and sent as X-CSRF-TOKEN on state-changing requests.
 */

export interface ProblemDetails {
  status: number;
  title?: string;
  code?: string;
  correlationId?: string;
  plane?: string;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly correlationId?: string;
  readonly fieldErrors: Record<string, string[]>;
  readonly retryAfterSeconds?: number;

  constructor(problem: ProblemDetails, retryAfterSeconds?: number) {
    super(problem.title ?? "İstek başarısız oldu.");
    this.status = problem.status;
    this.code = problem.code ?? (problem.status >= 500 ? "platform_error" : "request_failed");
    this.correlationId = problem.correlationId;
    this.fieldErrors = lowerKeys(problem.errors ?? {});
    this.retryAfterSeconds = retryAfterSeconds;
  }

  get isUnauthenticated() {
    return this.status === 401;
  }

  get isNotFound() {
    return this.status === 404;
  }
}

function lowerKeys(errors: Record<string, string[]>): Record<string, string[]> {
  const out: Record<string, string[]> = {};
  for (const [k, v] of Object.entries(errors)) {
    out[k.charAt(0).toLowerCase() + k.slice(1)] = v;
  }
  return out;
}

let csrfToken: string | null = null;

export function resetCsrf() {
  csrfToken = null;
}

async function getCsrf(): Promise<string> {
  if (csrfToken) return csrfToken;
  const res = await fetch("/api/auth/csrf", { credentials: "same-origin", cache: "no-store" });
  if (!res.ok) throw await toApiError(res);
  const json = (await res.json()) as { token: string };
  csrfToken = json.token;
  return csrfToken;
}

async function toApiError(res: Response): Promise<ApiError> {
  let problem: ProblemDetails = { status: res.status };
  try {
    const type = res.headers.get("content-type") ?? "";
    if (type.includes("json")) {
      problem = { ...(await res.json()), status: res.status };
    }
  } catch {
    // body was not JSON; keep the status-only problem
  }
  if (res.status === 502 || res.status === 504 || (res.status === 500 && !problem.code)) {
    problem.code = "api_unreachable";
    problem.title = "Kontrol API'sine ulaşılamıyor. Sunucu çalışıyor mu?";
  }
  const retry = Number(res.headers.get("retry-after"));
  return new ApiError(problem, Number.isFinite(retry) && retry > 0 ? retry : undefined);
}

type Method = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export interface RequestOptions {
  method?: Method;
  body?: unknown;
  signal?: AbortSignal;
}

export async function api<T>(path: string, options: RequestOptions = {}, retried = false): Promise<T> {
  const method = options.method ?? "GET";
  const headers: Record<string, string> = { Accept: "application/json" };
  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  if (method !== "GET") headers["X-CSRF-TOKEN"] = await getCsrf();

  const res = await fetch(path, {
    method,
    headers,
    body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
    credentials: "same-origin",
    cache: "no-store",
    signal: options.signal,
  });

  if (!res.ok) {
    const error = await toApiError(res);
    // Token is bound to the session identity; refresh once after login/logout or expiry.
    if (!retried && error.code === "csrf_failed") {
      resetCsrf();
      return api<T>(path, options, true);
    }
    throw error;
  }

  if (res.status === 204) return undefined as T;
  const type = res.headers.get("content-type") ?? "";
  return (type.includes("json") ? await res.json() : await res.text()) as T;
}

/** Fetches a file download (export) and triggers a browser save without exposing anything in the URL. */
export async function download(path: string, fallbackName: string): Promise<void> {
  const res = await fetch(path, { credentials: "same-origin", cache: "no-store" });
  if (!res.ok) throw await toApiError(res);
  const disposition = res.headers.get("content-disposition") ?? "";
  const match = /filename="?([A-Za-z0-9._-]+)"?/.exec(disposition);
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = match?.[1] ?? fallbackName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
