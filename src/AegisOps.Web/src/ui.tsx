import type { ReactNode } from "react";
import { humanize, statusTone, type Tone } from "./format";
import { IconAlert, IconInbox, IconInfo, IconX } from "./icons";

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <header className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {actions && <div className="page-actions">{actions}</div>}
    </header>
  );
}

/** Dot + label status pill. `label` defaults to the humanized status; the tone comes from the raw value. */
export function StatusBadge({
  status,
  label,
  tone,
  className,
}: {
  status: string;
  label?: string;
  tone?: Tone;
  className?: string;
}) {
  return (
    <span className={`badge badge-${tone ?? statusTone(status)}${className ? ` ${className}` : ""}`}>
      <span className="badge-dot" aria-hidden="true" />
      {label ?? humanize(status)}
    </span>
  );
}

export function SeverityBadge({ severity }: { severity: string }) {
  return <span className={`severity severity-${severity.toLowerCase()}`}>{severity}</span>;
}

/**
 * Inline alert. Danger alerts use role="alert"; others role="status". Pass only `children` when the text must be read
 * as-is (Problem Details title/detail); `title` adds a bold first line.
 */
export function Alert({
  tone = "danger",
  title,
  children,
  onDismiss,
  role,
  className,
}: {
  tone?: "danger" | "warning" | "info" | "success";
  title?: string;
  children?: ReactNode;
  onDismiss?: () => void;
  role?: "alert" | "status" | "note";
  className?: string;
}) {
  return (
    <div
      className={`alert alert-${tone}${className ? ` ${className}` : ""}`}
      role={role ?? (tone === "danger" ? "alert" : "status")}
    >
      {tone === "info" ? <IconInfo className="alert-icon" /> : <IconAlert className="alert-icon" />}
      <div className="alert-body">
        {title && <strong className="alert-title">{title}</strong>}
        {children}
      </div>
      {onDismiss && (
        <button type="button" className="icon-button alert-dismiss" aria-label="Dismiss" onClick={onDismiss}>
          <IconX />
        </button>
      )}
    </div>
  );
}

export function EmptyState({ children, action, icon }: { children: ReactNode; action?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="empty-state">
      <span className="empty-icon">{icon ?? <IconInbox />}</span>
      <p>{children}</p>
      {action}
    </div>
  );
}

export function Loading({ children = "Loading…" }: { children?: ReactNode }) {
  return (
    <p className="loading" role="status">
      <span className="spinner" aria-hidden="true" />
      {children}
    </p>
  );
}

export function Spinner() {
  return <span className="spinner" aria-hidden="true" />;
}

export function CardHeader({
  title,
  meta,
  actions,
  id,
}: {
  title: ReactNode;
  meta?: ReactNode;
  actions?: ReactNode;
  id?: string;
}) {
  return (
    <div className="card-header">
      <div className="card-title">
        <h2 id={id}>{title}</h2>
        {meta !== undefined && <span className="card-meta">{meta}</span>}
      </div>
      {actions && <div className="card-actions">{actions}</div>}
    </div>
  );
}

/** Mono, truncated identifier with the full value in a tooltip. */
export function Mono({ value, display }: { value: string; display?: string }) {
  return (
    <code className="mono" title={value}>
      {display ?? value}
    </code>
  );
}
