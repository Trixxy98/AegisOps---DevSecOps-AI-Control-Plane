import type { ReactNode } from "react";
import { pollingFallbackMs, type UpdateConnectionStatus } from "./deploymentUpdates";
import { IconMonitor, IconMoon, IconSignOut, IconSun, LogoMark } from "./icons";
import type { ThemePreference } from "./theme";

export type NavItem<T extends string> = { id: T; label: string; icon: ReactNode; count?: number };
export type NavGroup<T extends string> = { label: string; items: Array<NavItem<T>> };
export type CurrentUser = { displayName?: string | null; email?: string | null; roles?: string[] };

/** One small indicator for the SignalR state: live, or reconnecting while the views poll REST. */
export function ConnectionIndicator({ status }: { status: UpdateConnectionStatus }) {
  const label = status === "live" ? "Live" : status === "polling" ? "Reconnecting…" : "Connecting…";
  const detail =
    status === "live"
      ? "Live updates are connected."
      : status === "polling"
        ? `Live updates are unavailable; refreshing every ${pollingFallbackMs / 1000} s.`
        : "Connecting to live updates.";
  return (
    <span className={`connection connection-${status}`} role="status" title={detail}>
      <span className="connection-dot" aria-hidden="true" />
      {label}
    </span>
  );
}

const themeOptions: Array<{ value: ThemePreference; label: string; icon: ReactNode }> = [
  { value: "system", label: "System theme", icon: <IconMonitor size={14} /> },
  { value: "light", label: "Light theme", icon: <IconSun size={14} /> },
  { value: "dark", label: "Dark theme", icon: <IconMoon size={14} /> },
];

export function ThemeControl({
  value,
  onChange,
}: {
  value: ThemePreference;
  onChange: (value: ThemePreference) => void;
}) {
  return (
    <div className="theme-control" role="group" aria-label="Theme">
      {themeOptions.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-label={option.label}
          title={option.label}
          aria-pressed={value === option.value}
          onClick={() => onChange(option.value)}
        >
          {option.icon}
        </button>
      ))}
    </div>
  );
}

function initials(user: CurrentUser): string {
  const source = (user.displayName || user.email || "?").trim();
  const parts = source.split(/[\s@._-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? "?") + (parts.length > 1 && user.displayName ? (parts[1]?.[0] ?? "") : "")).toUpperCase();
}

export function Sidebar<T extends string>({
  groups,
  active,
  onNavigate,
  status,
  user,
  theme,
  onThemeChange,
  onSignOut,
}: {
  groups: Array<NavGroup<T>>;
  active: T;
  onNavigate: (id: T) => void;
  status: UpdateConnectionStatus;
  user: CurrentUser;
  theme: ThemePreference;
  onThemeChange: (value: ThemePreference) => void;
  onSignOut: () => void;
}) {
  const name = user.displayName || user.email || "Signed in";
  const roles = user.roles?.length ? user.roles.join(", ") : null;
  return (
    <aside className="sidebar">
      <div className="sidebar-inner">
        <div className="sidebar-brand">
          <span className="brand">
            <LogoMark />
            <span className="brand-name">AegisOps</span>
          </span>
          <ConnectionIndicator status={status} />
        </div>
        <nav className="sidebar-nav" aria-label="Main navigation">
          {groups.map((group) => (
            <div key={group.label} className="nav-group">
              <p className="nav-group-label">{group.label}</p>
              <ul>
                {group.items.map((item) => (
                  <li key={item.id}>
                    <button
                      className={`nav-item ${active === item.id ? "is-active" : ""}`}
                      aria-current={active === item.id ? "page" : undefined}
                      title={item.label}
                      onClick={() => onNavigate(item.id)}
                      type="button"
                    >
                      {item.icon}
                      <span className="nav-label">{item.label}</span>
                      {!!item.count && (
                        <span className="nav-count" aria-hidden="true">
                          {item.count > 99 ? "99+" : item.count}
                        </span>
                      )}
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </nav>
        <div className="sidebar-footer">
          <div className="user-card" title={user.email ?? undefined}>
            <span className="avatar" aria-hidden="true">
              {initials(user)}
            </span>
            <span className="user-text">
              <strong>{name}</strong>
              {user.email && user.email !== name && <span>{user.email}</span>}
              {roles && <span>{roles}</span>}
            </span>
          </div>
          <div className="sidebar-actions">
            <ThemeControl value={theme} onChange={onThemeChange} />
            <button className="btn btn-ghost btn-sm" type="button" onClick={onSignOut} title="Sign out">
              <IconSignOut size={14} />
              <span className="sign-out-label">Sign out</span>
            </button>
          </div>
        </div>
      </div>
    </aside>
  );
}
