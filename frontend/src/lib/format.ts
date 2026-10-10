const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "medium" });
const time = new Intl.DateTimeFormat("tr-TR", { hour: "2-digit", minute: "2-digit", second: "2-digit", fractionalSecondDigits: 3 });

/** Stored in UTC, displayed in the viewer's local time zone. */
export function formatDateTime(iso: string | null | undefined): string {
  return iso ? dateTime.format(new Date(iso)) : "—";
}

export function formatTime(iso: string): string {
  return time.format(new Date(iso));
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(ms < 10000 ? 2 : 0)} sn`;
}

export function formatRemaining(seconds: number): string {
  if (seconds <= 0) return "0:00";
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}

export function shortId(id: string): string {
  return id.slice(0, 8);
}
