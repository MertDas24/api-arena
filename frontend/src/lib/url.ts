/**
 * Only same-site relative paths are accepted as post-login destinations (AUTH-10).
 * Rejects absolute URLs, protocol-relative ("//evil"), backslash tricks ("/\\evil"), and control characters.
 */
export function safeReturnUrl(candidate: string | null | undefined, fallback = "/dashboard"): string {
  if (!candidate) return fallback;
  let value: string;
  try {
    value = decodeURIComponent(candidate);
  } catch {
    return fallback;
  }
  if (value.length > 512) return fallback;
  if (!value.startsWith("/")) return fallback;
  if (value.startsWith("//") || value.startsWith("/\\")) return fallback;
  if (/[\u0000-\u001f\u007f\\]/.test(value)) return fallback;
  if (/^\/(login|register|logout)(\/|\?|$)/.test(value)) return fallback;
  return value;
}
