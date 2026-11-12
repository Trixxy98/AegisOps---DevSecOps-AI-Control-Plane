import { useEffect, useEffectEvent, useRef, useState, type ReactNode } from "react";
import { AiGovernance } from "./AiGovernance";
import { AiPlayground } from "./AiPlayground";
import { Administration } from "./Administration";
import { api } from "./api";
import { Deployments } from "./Deployments";
import {
  connectDeploymentUpdates,
  pollingFallbackMs,
  shouldPoll,
  type UpdateConnectionStatus,
} from "./deploymentUpdates";
import { Findings } from "./Findings";
import { formatDateTime, humanize, shortId, statusTone } from "./format";
import {
  IconApprovals,
  IconAudit,
  IconChat,
  IconCheck,
  IconFindings,
  IconFolder,
  IconOverview,
  IconPolicies,
  IconRocket,
  IconSettings,
  IconSparkles,
  IconAlert,
  IconX,
} from "./icons";
import { Login } from "./Login";
import { Policies } from "./Policies";
import { Projects, type DeployTarget } from "./Projects";
import { Sidebar, type CurrentUser, type NavGroup } from "./Shell";
import { useThemePreference } from "./theme";
import { Alert, CardHeader, EmptyState, Mono, PageHeader, StatusBadge } from "./ui";

type View =
  | "dashboard"
  | "projects"
  | "deployments"
  | "approvals"
  | "findings"
  | "policies"
  | "ai"
  | "ai-chat"
  | "audit"
  | "admin";
type DeploymentToast = { id: number; message: string; type: "success" | "error"; approvalId?: string };
type ApprovalItem = { id: string; projectId: string; requestedAt: string; status: string };
type DependencyHealth = { ollama: string; docker: string; worker: string };
const approvalQueuePath = "/api/v1/deployments/approval-queue";

// `permission` entries are shown only when /auth/me grants that permission (the API still enforces it).
const navigation: Array<{
  id: View;
  label: string;
  description: string;
  group: string;
  icon: ReactNode;
  permission?: string;
}> = [
  {
    id: "dashboard",
    label: "Overview",
    description: "Deployment activity at a glance.",
    group: "Deliver",
    icon: <IconOverview />,
  },
  {
    id: "projects",
    label: "Projects",
    description: "Environments, artifacts and CI keys.",
    group: "Deliver",
    icon: <IconFolder />,
  },
  {
    id: "deployments",
    label: "Deployments",
    description: "Releases, policy checks and timelines.",
    group: "Deliver",
    icon: <IconRocket />,
  },
  {
    id: "approvals",
    label: "Approvals",
    description: "Deployments waiting for your decision.",
    group: "Deliver",
    icon: <IconApprovals />,
  },
  {
    id: "findings",
    label: "Findings",
    description: "Security findings from CI scans.",
    group: "Security",
    icon: <IconFindings />,
    permission: "findings:read",
  },
  {
    id: "policies",
    label: "Policies",
    description: "Rules that gate every deployment.",
    group: "Security",
    icon: <IconPolicies />,
  },
  {
    id: "ai",
    label: "AI Governance",
    description: "AI usage history and policy controls.",
    group: "AI",
    icon: <IconSparkles />,
  },
  {
    id: "ai-chat",
    label: "AI Playground",
    description: "Chat through the governed AI gateway.",
    group: "AI",
    icon: <IconChat />,
    permission: "ai:chat",
  },
  { id: "audit", label: "Audit log", description: "Who did what, and when.", group: "Admin", icon: <IconAudit /> },
  {
    id: "admin",
    label: "Administration",
    description: "Users, teams and background jobs.",
    group: "Admin",
    icon: <IconSettings />,
  },
];

export function App() {
  const [token, setToken] = useState<string | null>(null);
  const [view, setView] = useState<View>("dashboard");
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  const [updateStatus, setUpdateStatus] = useState<UpdateConnectionStatus>("connecting");
  const [pendingDeployments, setPendingDeployments] = useState<string[]>([]);
  const pendingDeploymentsRef = useRef(new Set<string>());
  const [deploymentToast, setDeploymentToast] = useState<DeploymentToast | null>(null);
  const toastId = useRef(0);
  const [approvalItems, setApprovalItems] = useState<ApprovalItem[]>([]);
  const [dependencyHealth, setDependencyHealth] = useState<DependencyHealth | null>(null);
  const approvalHint = useRef<(id?: string) => void>(() => {});
  const [permissions, setPermissions] = useState<string[]>([]);
  const [currentUserId, setCurrentUserId] = useState<string | null>(null);
  const [currentUser, setCurrentUser] = useState<CurrentUser>({});
  const [theme, setTheme] = useThemePreference();
  const visibleNavigation = navigation.filter((item) => !item.permission || permissions.includes(item.permission));

  useEffect(() => {
    if (!token) return;
    let active = true;
    api<{ id?: string; permissions?: string[]; email?: string; displayName?: string; roles?: string[] }>(
      "/api/v1/auth/me",
      token,
    )
      .then((me) => {
        if (!active) return;
        setPermissions(me.permissions ?? []);
        setCurrentUserId(me.id ?? null);
        setCurrentUser({ displayName: me.displayName, email: me.email, roles: me.roles });
      })
      .catch(() => {
        if (active) setPermissions([]);
      });
    return () => {
      active = false;
    };
  }, [token]);

  useEffect(() => {
    if (!deploymentToast) return;
    const timer = setTimeout(
      () => setDeploymentToast((current) => (current?.id === deploymentToast.id ? null : current)),
      8000,
    );
    return () => clearTimeout(timer);
  }, [deploymentToast]);

  async function requestDeployment(artifact: { id: string; version: string }, environment: DeployTarget) {
    if (!token) return;
    const key = `${artifact.id}:${environment.id}`;
    if (pendingDeploymentsRef.current.has(key)) return;
    if (
      ["staging", "production"].includes(environment.tier.toLowerCase()) &&
      !window.confirm(`Deploy ${artifact.version} to ${environment.name} (${environment.tier})?`)
    )
      return;

    pendingDeploymentsRef.current.add(key);
    setPendingDeployments((current) => [...current, key]);
    try {
      const deployment = await api<{ id: string; status: string }>("/api/v1/deployments", token, {
        method: "POST",
        body: JSON.stringify({ artifactId: artifact.id, environmentId: environment.id, reason: "UI request" }),
      });
      setDeploymentToast({
        id: ++toastId.current,
        type: "success",
        message: `Deployment requested for ${artifact.version} to ${environment.name}. ID: ${deployment.id} · Status: ${deployment.status}`,
      });
      setError(null);
    } catch (error) {
      setDeploymentToast({
        id: ++toastId.current,
        type: "error",
        message: `Could not request deployment of ${artifact.version} to ${environment.name}: ${error instanceof Error ? error.message : "Please try again."}`,
      });
    } finally {
      pendingDeploymentsRef.current.delete(key);
      setPendingDeployments((current) => current.filter((item) => item !== key));
    }
  }

  useEffect(() => {
    if (!token) return;
    return connectDeploymentUpdates(
      token,
      () => {
        setRevision((value) => value + 1);
        approvalHint.current();
      },
      // Becoming live is always followed by one `invalidate` (the catch-up), which also refreshes approvals.
      (status) => setUpdateStatus(status),
      (id) => approvalHint.current(id),
    );
  }, [token]);

  useEffect(() => {
    if (!token) return;
    let active = true;
    let initialized = false;
    let sequence = 0;
    const seen = new Set<string>();
    const refresh = async (hint?: string) => {
      const current = ++sequence;
      try {
        const items = await api<ApprovalItem[]>(approvalQueuePath, token);
        if (!active || current !== sequence) return;
        setApprovalItems(items);
        const available = new Set(items.map((item) => item.id));
        setDeploymentToast((toast) => (toast?.approvalId && !available.has(toast.approvalId) ? null : toast));
        const fresh = initialized ? items.find((item) => !seen.has(item.id) && (!hint || item.id === hint)) : undefined;
        if (fresh) {
          setDeploymentToast({
            id: ++toastId.current,
            type: "success",
            approvalId: fresh.id,
            message: `Approval needed for deployment ${fresh.id} (project ${fresh.projectId}).`,
          });
        }
        items.forEach((item) => seen.add(item.id));
        initialized = true;
      } catch {
        if (active && current === sequence) {
          setApprovalItems([]);
          setDeploymentToast((toast) => (toast?.approvalId ? null : toast));
        }
      }
    };
    approvalHint.current = (id) => void refresh(id);
    void refresh();
    const onFocus = () => void refresh();
    const onVisibility = () => {
      if (document.visibilityState === "visible") void refresh();
    };
    window.addEventListener("focus", onFocus);
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      active = false;
      approvalHint.current = () => {};
      window.removeEventListener("focus", onFocus);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [token]);

  const polling = shouldPoll(updateStatus);
  // Fallback only: while the hub is live, approval changes arrive as ApprovalQueueChanged/DeploymentChanged.
  useEffect(() => {
    if (!token || !polling) return;
    const timer = setInterval(() => approvalHint.current(), pollingFallbackMs);
    return () => clearInterval(timer);
  }, [token, polling]);

  useEffect(() => {
    if (!token) return;
    let active = true;
    let sequence = 0;
    const refresh = async () => {
      const current = ++sequence;
      try {
        const health = await api<DependencyHealth>("/api/v1/system/dependencies", token);
        if (active && current === sequence) setDependencyHealth(health);
      } catch {
        if (active && current === sequence)
          setDependencyHealth({ ollama: "Unknown", docker: "Unknown", worker: "Unreachable" });
      }
    };
    void refresh();
    const timer = setInterval(() => void refresh(), 30000);
    const onFocus = () => void refresh();
    window.addEventListener("focus", onFocus);
    return () => {
      active = false;
      clearInterval(timer);
      window.removeEventListener("focus", onFocus);
    };
  }, [token]);

  const healthWarnings = dependencyHealth
    ? [
        dependencyHealth.ollama === "Degraded" || dependencyHealth.ollama === "Unhealthy"
          ? "Ollama unavailable — AI features may be limited."
          : null,
        dependencyHealth.docker === "Degraded" || dependencyHealth.docker === "Unhealthy"
          ? "Docker unavailable — deployments may be limited."
          : null,
        dependencyHealth.worker === "Unhealthy" ? "Worker unhealthy — background processing may be limited." : null,
        dependencyHealth.worker === "Unreachable"
          ? "Worker status unavailable — Docker status cannot be confirmed."
          : null,
      ].filter((message): message is string => message !== null)
    : [];

  // Session-scoped state is reset when the session ends, so the next sign-in starts clean.
  function signOut() {
    setDeploymentToast(null);
    setApprovalItems([]);
    setDependencyHealth(null);
    setPermissions([]);
    setCurrentUser({});
    setUpdateStatus("connecting");
    setToken(null);
  }

  if (!token) {
    return <Login onToken={setToken} />;
  }

  const groups: Array<NavGroup<View>> = [];
  for (const item of visibleNavigation) {
    let group = groups.find((entry) => entry.label === item.group);
    if (!group) {
      group = { label: item.group, items: [] };
      groups.push(group);
    }
    group.items.push({
      id: item.id,
      label: item.label,
      icon: item.icon,
      count: item.id === "approvals" ? approvalItems.length : undefined,
    });
  }
  const current = navigation.find((item) => item.id === view);
  const navigate = (next: View) => {
    setView(next);
    setError(null);
  };

  return (
    <div className="app-shell">
      <Sidebar
        groups={groups}
        active={view}
        onNavigate={navigate}
        status={updateStatus}
        user={currentUser}
        theme={theme}
        onThemeChange={setTheme}
        onSignOut={signOut}
      />
      <div className="main-column">
        <main className="content">
          <PageHeader title={current?.label ?? ""} subtitle={current?.description} />
          {healthWarnings.length > 0 && (
            <Alert tone="warning" title="System degraded" role="status">
              <ul>
                {healthWarnings.map((message) => (
                  <li key={message}>{message}</li>
                ))}
              </ul>
            </Alert>
          )}
          {error && (
            <Alert title="Request failed" onDismiss={() => setError(null)}>
              {error}
            </Alert>
          )}
          {view === "dashboard" && (
            <Dashboard
              token={token}
              onError={setError}
              revision={revision}
              polling={polling}
              approvals={approvalItems.length}
              health={dependencyHealth}
              onNavigate={navigate}
            />
          )}
          {view === "projects" && (
            <Projects
              token={token}
              permissions={permissions}
              onError={setError}
              pendingDeployments={pendingDeployments}
              onDeploy={requestDeployment}
            />
          )}
          {view === "deployments" && (
            <Deployments
              token={token}
              onError={setError}
              revision={revision}
              polling={polling}
              onNavigate={() => navigate("projects")}
            />
          )}
          {view === "approvals" && (
            <Approvals token={token} onError={setError} items={approvalItems} refresh={() => approvalHint.current()} />
          )}
          {view === "findings" && <Findings token={token} permissions={permissions} />}
          {view === "policies" && <Policies token={token} onError={setError} />}
          {view === "ai" && <AiGovernance token={token} />}
          {view === "ai-chat" && <AiPlayground token={token} />}
          {view === "audit" && <Audit token={token} onError={setError} />}
          {view === "admin" && (
            <Administration token={token} onError={setError} permissions={permissions} currentUserId={currentUserId} />
          )}
        </main>
      </div>
      {deploymentToast && (
        <div
          className={`toast toast-${deploymentToast.type}`}
          role={deploymentToast.type === "error" ? "alert" : "status"}
        >
          {deploymentToast.type === "error" ? (
            <IconAlert className="toast-icon" />
          ) : (
            <IconCheck className="toast-icon" />
          )}
          <span>{deploymentToast.message}</span>
          <button
            type="button"
            className="icon-button"
            aria-label="Dismiss deployment notification"
            onClick={() => setDeploymentToast(null)}
          >
            <IconX />
          </button>
        </div>
      )}
    </div>
  );
}

const serviceLabels: Array<{ key: keyof DependencyHealth; label: string }> = [
  { key: "worker", label: "Worker" },
  { key: "docker", label: "Docker" },
  { key: "ollama", label: "Ollama (AI)" },
];

function Dashboard({
  token,
  onError,
  revision,
  polling,
  approvals,
  health,
  onNavigate,
}: {
  token: string;
  onError: (value: string | null) => void;
  revision: number;
  polling: boolean;
  approvals: number;
  health: DependencyHealth | null;
  onNavigate: (view: View) => void;
}) {
  const [counts, setCounts] = useState<Record<string, number>>({});
  const load = useEffectEvent(() =>
    api<Array<{ status: string }>>("/api/v1/deployments", token)
      .then((items) => {
        const next: Record<string, number> = {};
        for (const item of items) {
          next[item.status] = (next[item.status] ?? 0) + 1;
        }
        setCounts(next);
      })
      .catch((error: Error) => onError(error.message)),
  );
  // Initial load and every hub invalidation (revision) reload once.
  useEffect(() => {
    void load();
  }, [token, revision]);
  // Timer only while the hub is disconnected or reconnecting.
  useEffect(() => {
    if (!polling) return;
    const timer = setInterval(() => void load(), pollingFallbackMs);
    return () => clearInterval(timer);
  }, [token, polling]);

  const entries = Object.entries(counts);
  return (
    <div className="stack-lg">
      {entries.length > 0 ? (
        <div className="stat-grid">
          {entries.map(([status, count]) => (
            <article key={status} className="stat-card">
              <p>
                <span className={`stat-dot tone-${statusTone(status)}`} aria-hidden="true" />
                {humanize(status)}
              </p>
              <strong>{count}</strong>
            </article>
          ))}
        </div>
      ) : (
        <section className="card">
          <EmptyState
            action={
              <button className="btn btn-secondary btn-sm" type="button" onClick={() => onNavigate("projects")}>
                Open projects
              </button>
            }
          >
            No deployments yet. Deploy an artifact from a project to see activity here.
          </EmptyState>
        </section>
      )}
      <div className="split-even split overview-cards">
        <section className="card">
          <CardHeader
            title="Your approvals"
            actions={
              <button className="btn btn-secondary btn-sm" type="button" onClick={() => onNavigate("approvals")}>
                Open approvals
              </button>
            }
          />
          <div className="overview-approvals">
            <strong>{approvals}</strong>
            <p className="muted">
              {approvals === 0
                ? "Nothing is waiting for your decision."
                : `deployment${approvals === 1 ? " is" : "s are"} waiting for your decision.`}
            </p>
          </div>
        </section>
        <section className="card">
          <CardHeader title="Services" />
          <ul className="list service-list">
            {serviceLabels.map((service) => (
              <li key={service.key}>
                <span>{service.label}</span>
                {health ? <StatusBadge status={health[service.key]} /> : <span className="subtle">Checking…</span>}
              </li>
            ))}
          </ul>
        </section>
      </div>
    </div>
  );
}

function Approvals({
  token,
  onError,
  items,
  refresh,
}: {
  token: string;
  onError: (value: string | null) => void;
  items: ApprovalItem[];
  refresh: () => void;
}) {
  async function decide(id: string, decision: "Approved" | "Rejected") {
    await api(`/api/v1/deployments/${id}/approvals`, token, {
      method: "POST",
      body: JSON.stringify({ decision, comment: decision }),
    });
    refresh();
  }

  return (
    <section className="card">
      <CardHeader title="Waiting for approval" meta={items.length} />
      {items.length === 0 ? (
        <EmptyState icon={<IconApprovals />}>No requests awaiting approval.</EmptyState>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Deployment</th>
                <th scope="col">Project</th>
                <th scope="col">Requested</th>
                <th scope="col">Status</th>
                <th scope="col" className="actions">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.id}>
                  <td>
                    <Mono value={item.id} />
                  </td>
                  <td>
                    <Mono value={item.projectId} display={shortId(item.projectId)} />
                  </td>
                  <td className="nowrap tabular">
                    <time dateTime={item.requestedAt}>{formatDateTime(item.requestedAt)}</time>
                  </td>
                  <td>
                    <StatusBadge status={item.status} />
                  </td>
                  <td className="actions">
                    <span className="row">
                      <button
                        className="btn btn-primary btn-sm"
                        type="button"
                        onClick={() => void decide(item.id, "Approved").catch((error: Error) => onError(error.message))}
                      >
                        Approve
                      </button>
                      <button
                        className="btn btn-danger btn-sm"
                        type="button"
                        onClick={() => void decide(item.id, "Rejected").catch((error: Error) => onError(error.message))}
                      >
                        Reject
                      </button>
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function Audit({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<
    Array<{ id: string; timestamp: string; action: string; outcome: string; actorDisplay: string }>
  >([]);
  useEffect(() => {
    api<typeof items>("/api/v1/audit", token)
      .then(setItems)
      .catch((error: Error) => onError(error.message));
  }, [token, onError]);
  return (
    <section className="card">
      <CardHeader title="Recent activity" meta={items.length ? `Latest ${items.length}` : undefined} />
      {items.length === 0 ? (
        <EmptyState icon={<IconAudit />}>No audit activity to show yet.</EmptyState>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">Actor</th>
                <th scope="col">Action</th>
                <th scope="col">Outcome</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.id}>
                  <td className="nowrap tabular">
                    <time dateTime={item.timestamp} title={item.timestamp}>
                      {formatDateTime(item.timestamp)}
                    </time>
                  </td>
                  <td className="truncate" title={item.actorDisplay}>
                    {item.actorDisplay}
                  </td>
                  <td>
                    <code className="mono">{item.action}</code>
                  </td>
                  <td>
                    <StatusBadge status={item.outcome} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
