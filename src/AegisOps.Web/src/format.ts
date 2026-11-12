export type Tone = "neutral" | "success" | "info" | "warning" | "danger";

/** "AwaitingApproval" → "Awaiting approval", "DeadLettered" → "Dead lettered". Display only; API values are unchanged. */
export function humanize(value: string): string {
  const words = value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(" ");
  return words.map((word, index) => (index === 0 || /^[A-Z0-9]+$/.test(word) ? word : word.toLowerCase())).join(" ");
}

const tones: Record<string, Tone> = {
  succeeded: "success",
  healthy: "success",
  approved: "success",
  allow: "success",
  allowed: "success",
  completed: "success",
  delivered: "success",
  resolved: "success",
  pass: "success",
  active: "success",
  configured: "success",
  requested: "info",
  evaluating: "info",
  deploying: "info",
  running: "info",
  open: "info",
  awaitingapproval: "warning",
  requireapproval: "warning",
  pending: "warning",
  degraded: "warning",
  redact: "warning",
  suppressed: "neutral",
  failed: "danger",
  failure: "danger",
  denied: "danger",
  deny: "danger",
  rejected: "danger",
  blocked: "danger",
  block: "danger",
  unhealthy: "danger",
  unreachable: "danger",
  deadlettered: "danger",
  fail: "danger",
};

export function statusTone(status: string): Tone {
  return tones[status.toLowerCase()] ?? "neutral";
}

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

/** Formats an ISO timestamp for tables; returns the raw value when it cannot be parsed. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? dateTime.format(timestamp) : value;
}

/** Last 8 characters of a GUID (the random part of a v7 GUID) for compact display; show the full value in a tooltip. */
export function shortId(value: string): string {
  return value.length > 8 ? value.slice(-8) : value;
}
