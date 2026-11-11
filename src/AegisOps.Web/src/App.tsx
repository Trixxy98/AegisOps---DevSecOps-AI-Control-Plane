import { useEffect, useEffectEvent, useRef, useState } from "react";
import { AiPlayground } from "./AiPlayground";
import { api } from "./api";
import {
  connectDeploymentUpdates,
  pollingFallbackMs,
  shouldPoll,
  type UpdateConnectionStatus,
} from "./deploymentUpdates";
import { Findings } from "./Findings";

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
type DeployTarget = { id: string; name: string; tier: string };
type DeploymentToast = { id: number; message: string; type: "success" | "error"; approvalId?: string };
type ApprovalItem = { id: string; projectId: string; requestedAt: string; status: string };
type DependencyHealth = { ollama: string; docker: string; worker: string };
const approvalQueuePath = "/api/v1/deployments/approval-queue";

// `permission` entries are shown only when /auth/me grants that permission (the API still enforces it).
const navigation: Array<{ id: View; label: string; description: string; permission?: string }> = [
  { id: "dashboard", label: "Overview", description: "Deployment activity at a glance" },
  { id: "projects", label: "Projects", description: "Environments, artifacts, and access" },
  { id: "deployments", label: "Deployments", description: "Track releases and review outcomes" },
  { id: "approvals", label: "Approvals", description: "Review requests awaiting a decision" },
  {
    id: "findings",
    label: "Findings",
    description: "Explore and triage security findings",
    permission: "findings:read",
  },
  { id: "policies", label: "Policies", description: "Manage deployment guardrails" },
  { id: "ai", label: "AI Governance", description: "Review governed AI usage and controls" },
  { id: "ai-chat", label: "AI Playground", description: "Chat through the governed AI gateway", permission: "ai:chat" },
  { id: "audit", label: "Audit log", description: "A record of platform activity" },
  { id: "admin", label: "Administration", description: "People, teams, and background jobs" },
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
  const visibleNavigation = navigation.filter((item) => !item.permission || permissions.includes(item.permission));

  useEffect(() => {
    if (!token) return;
    let active = true;
    api<{ permissions?: string[] }>("/api/v1/auth/me", token)
      .then((me) => {
        if (active) setPermissions(me.permissions ?? []);
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
    setUpdateStatus("connecting");
    setToken(null);
  }

  if (!token) {
    return <Login onToken={setToken} />;
  }

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">
            A
          </span>
          <div>
            <strong>AegisOps</strong>
            <span>CONTROL PLANE</span>
          </div>
        </div>
        <div className="sidebar-label">WORKSPACE</div>
        <nav className="navigation" aria-label="Main navigation">
          {visibleNavigation.map((item) => (
            <button
              key={item.id}
              className={`nav-item ${view === item.id ? "is-active" : ""}`}
              aria-current={view === item.id ? "page" : undefined}
              onClick={() => {
                setView(item.id);
                setError(null);
              }}
              type="button"
            >
              {item.label}
            </button>
          ))}
        </nav>
        <div className="sidebar-footer">
          <span className="status-dot" /> System workspace
        </div>
      </aside>
      <div className="main-column">
        <header className="topbar">
          <span className="topbar-label">
            Workspace <span>/</span> {navigation.find((item) => item.id === view)?.label}
          </span>
          <div className="topbar-actions">
            <span className="update-status" role="status">
              <span className={`status-dot ${updateStatus !== "live" ? "status-dot-muted" : ""}`} />{" "}
              {updateStatus === "live"
                ? "Live updates"
                : updateStatus === "polling"
                  ? `Polling (${pollingFallbackMs / 1000}s)`
                  : "Connecting"}
            </span>
            <button className="logout-button" type="button" onClick={signOut}>
              Sign out
            </button>
          </div>
        </header>
        <div className="mobile-navigation">
          <nav className="navigation" aria-label="Main navigation">
            {visibleNavigation.map((item) => (
              <button
                key={item.id}
                className={`nav-item ${view === item.id ? "is-active" : ""}`}
                aria-current={view === item.id ? "page" : undefined}
                onClick={() => {
                  setView(item.id);
                  setError(null);
                }}
                type="button"
              >
                {item.label}
              </button>
            ))}
          </nav>
        </div>
        <section className="content">
          <div className="page-heading">
            <div>
              <p className="eyebrow">AEGISOPS / WORKSPACE</p>
              <h1>{navigation.find((item) => item.id === view)?.label}</h1>
              <p className="page-description">{navigation.find((item) => item.id === view)?.description}</p>
            </div>
            <span className="heading-badge">
              <span className="status-dot" /> Live workspace
            </span>
          </div>
          {healthWarnings.length > 0 && (
            <div role="status" className="health-banner">
              <strong>System degraded</strong>
              <ul>
                {healthWarnings.map((message) => (
                  <li key={message}>{message}</li>
                ))}
              </ul>
            </div>
          )}
          {error && (
            <p role="alert" className="error-banner">
              {error}
            </p>
          )}
          {view === "dashboard" && <Dashboard token={token} onError={setError} revision={revision} polling={polling} />}
          {view === "projects" && (
            <Projects
              token={token}
              onError={setError}
              pendingDeployments={pendingDeployments}
              onDeploy={requestDeployment}
            />
          )}
          {view === "deployments" && (
            <Deployments token={token} onError={setError} revision={revision} polling={polling} />
          )}
          {view === "approvals" && (
            <Approvals token={token} onError={setError} items={approvalItems} refresh={() => approvalHint.current()} />
          )}
          {view === "findings" && <Findings token={token} permissions={permissions} />}
          {view === "policies" && <Policies token={token} onError={setError} />}
          {view === "ai" && <AiGovernance token={token} />}
          {view === "ai-chat" && <AiPlayground token={token} />}
          {view === "audit" && <Audit token={token} onError={setError} />}
          {view === "admin" && <Admin token={token} onError={setError} />}
        </section>
      </div>
      {deploymentToast && (
        <div
          className={`deployment-toast deployment-toast-${deploymentToast.type}`}
          role={deploymentToast.type === "error" ? "alert" : "status"}
        >
          <span>{deploymentToast.message}</span>
          <button
            type="button"
            className="toast-dismiss"
            aria-label="Dismiss deployment notification"
            onClick={() => setDeploymentToast(null)}
          >
            ×
          </button>
        </div>
      )}
    </main>
  );
}

function Login({ onToken }: { onToken: (token: string) => void }) {
  const [email, setEmail] = useState("admin@aegisops.local");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  // Set when the account still has a temporary password (docs/09): the workspace opens only after it is changed.
  const [pendingToken, setPendingToken] = useState<string | null>(null);
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  async function login(secret: string) {
    const response = await fetch("/api/v1/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password: secret }),
    });
    const body = (await response.json().catch(() => ({}))) as { title?: string; accessToken?: string };
    if (!response.ok || !body.accessToken) throw new Error(body.title ?? "Login failed");
    return body.accessToken;
  }

  async function signIn() {
    setError(null);
    try {
      const token = await login(password);
      // If /auth/me cannot be read, continue as before: the API still refuses temporary-password tokens.
      const me = await api<{ mustChangePassword?: boolean }>("/api/v1/auth/me", token).catch(() => null);
      if (me?.mustChangePassword) {
        setPendingToken(token);
        return;
      }
      onToken(token);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Login failed");
    }
  }

  async function changePassword() {
    setError(null);
    if (newPassword.length < 12) {
      setError("The new password must be at least 12 characters.");
      return;
    }
    if (newPassword !== confirmPassword) {
      setError("The new passwords do not match.");
      return;
    }
    if (newPassword === password) {
      setError("Choose a password different from the temporary one.");
      return;
    }
    try {
      await api("/api/v1/auth/change-password", pendingToken!, {
        method: "POST",
        body: JSON.stringify({ currentPassword: password, newPassword }),
      });
      // A fresh token no longer carries the password-change requirement.
      onToken(await login(newPassword));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Password change failed");
    }
  }

  const brand = (
    <div className="brand">
      <span className="brand-mark" aria-hidden="true">
        A
      </span>
      <div>
        <strong>AegisOps</strong>
        <span>CONTROL PLANE</span>
      </div>
    </div>
  );

  if (pendingToken) {
    return (
      <main className="login-page">
        <form
          className="login-card"
          onSubmit={(event) => {
            event.preventDefault();
            void changePassword();
          }}
        >
          {brand}
          <div className="login-heading">
            <p className="eyebrow">FIRST SIGN-IN</p>
            <h1>Choose a new password</h1>
            <p>Your account has a temporary password. Set your own (at least 12 characters) to continue.</p>
          </div>
          {error && (
            <p role="alert" className="error-banner">
              {error}
            </p>
          )}
          <label className="field-label" htmlFor="new-password">
            New password
          </label>
          <input
            id="new-password"
            autoComplete="new-password"
            type="password"
            value={newPassword}
            onChange={(event) => setNewPassword(event.target.value)}
          />
          <label className="field-label" htmlFor="confirm-password">
            Confirm new password
          </label>
          <input
            id="confirm-password"
            autoComplete="new-password"
            type="password"
            value={confirmPassword}
            onChange={(event) => setConfirmPassword(event.target.value)}
          />
          <button className="primary-button login-submit" type="submit">
            Set password and continue
          </button>
        </form>
      </main>
    );
  }

  return (
    <main className="login-page">
      <form
        className="login-card"
        onSubmit={(event) => {
          event.preventDefault();
          void signIn();
        }}
      >
        {brand}
        <div className="login-heading">
          <p className="eyebrow">WELCOME BACK</p>
          <h1>Sign in to your workspace</h1>
          <p>Manage deployments securely from one place.</p>
        </div>
        {error && (
          <p role="alert" className="error-banner">
            {error}
          </p>
        )}
        <label className="field-label" htmlFor="login-email">
          Email address
        </label>
        <input
          id="login-email"
          autoComplete="username"
          type="email"
          value={email}
          onChange={(event) => setEmail(event.target.value)}
        />
        <label className="field-label" htmlFor="login-password">
          Password
        </label>
        <input
          id="login-password"
          autoComplete="current-password"
          type="password"
          value={password}
          onChange={(event) => setPassword(event.target.value)}
        />
        <button className="primary-button login-submit" type="submit">
          Continue
        </button>
      </form>
    </main>
  );
}

function Dashboard({
  token,
  onError,
  revision,
  polling,
}: {
  token: string;
  onError: (value: string | null) => void;
  revision: number;
  polling: boolean;
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

  return (
    <div className="stat-grid">
      {Object.entries(counts).map(([status, count]) => (
        <article key={status} className="stat-card">
          <p>{status}</p>
          <strong>{count}</strong>
        </article>
      ))}
      {Object.keys(counts).length === 0 && <p className="empty-state">No deployments to show yet.</p>}
    </div>
  );
}

function Projects({
  token,
  onError,
  pendingDeployments,
  onDeploy,
}: {
  token: string;
  onError: (value: string | null) => void;
  pendingDeployments: string[];
  onDeploy: (artifact: { id: string; version: string }, environment: DeployTarget) => Promise<void>;
}) {
  const [projects, setProjects] = useState<Array<{ slug: string; name: string }>>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<{
    environments: Array<{
      id: string;
      name: string;
      tier: string;
      targetType: string;
      containerName?: string;
      network?: string;
      hostPort: number;
      containerPort: number;
      healthPath?: string;
      healthTimeoutSeconds: number;
    }>;
  } | null>(null);
  const [runtime, setRuntime] = useState<string | null>(null);
  const [artifacts, setArtifacts] = useState<Array<{ id: string; version: string; branch: string }>>([]);
  const [keys, setKeys] = useState<
    Array<{ id: string; name: string; keyPrefix: string; lastUsedAt?: string; revokedAt?: string }>
  >([]);
  const [keyName, setKeyName] = useState("ci");
  const [plaintext, setPlaintext] = useState<string | null>(null);

  useEffect(() => {
    api<Array<{ slug: string; name: string }>>("/api/v1/projects", token)
      .then(setProjects)
      .catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function open(slug: string) {
    setSelected(slug);
    const [project, items, apiKeys] = await Promise.all([
      api<{
        environments: Array<{
          id: string;
          name: string;
          tier: string;
          targetType: string;
          containerName?: string;
          network?: string;
          hostPort: number;
          containerPort: number;
          healthPath?: string;
          healthTimeoutSeconds: number;
        }>;
      }>(`/api/v1/projects/${slug}`, token),
      api<Array<{ id: string; version: string; branch: string }>>(`/api/v1/projects/${slug}/artifacts`, token),
      api<Array<{ id: string; name: string; keyPrefix: string; lastUsedAt?: string; revokedAt?: string }>>(
        `/api/v1/projects/${slug}/api-keys`,
        token,
      ).catch(() => []),
    ]);
    setDetail(project);
    setArtifacts(items);
    setKeys(apiKeys);
    setPlaintext(null);
  }

  async function createKey() {
    if (!selected) {
      return;
    }
    const expiresAt = new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString();
    const created = await api<{ plaintext: string }>(`/api/v1/projects/${selected}/api-keys`, token, {
      method: "POST",
      body: JSON.stringify({
        name: keyName,
        scopes: ["artifacts:write", "scans:write", "deployments:request"],
        expiresAt,
      }),
    });
    setPlaintext(created.plaintext);
    await open(selected);
    setPlaintext(created.plaintext);
  }

  async function revokeKey(id: string) {
    if (!selected) {
      return;
    }
    await api(`/api/v1/projects/${selected}/api-keys/${id}`, token, { method: "DELETE" });
    await open(selected);
  }

  return (
    <div className="split-layout">
      <section className="panel">
        <div className="panel-heading">
          <h2>Projects</h2>
          <span>{projects.length} total</span>
        </div>
        <ul className="item-list">
          {projects.map((project) => (
            <li key={project.slug}>
              <button
                className={`list-button ${selected === project.slug ? "is-selected" : ""}`}
                type="button"
                onClick={() => void open(project.slug)}
              >
                <strong>{project.name}</strong>
                <span>{project.slug}</span>
              </button>
            </li>
          ))}
        </ul>
        {projects.length === 0 && <p className="empty-state">No projects available.</p>}
      </section>
      {selected && detail && (
        <div className="detail-stack">
          <div className="detail-heading">
            <p className="eyebrow">PROJECT DETAILS</p>
            <h2>{selected}</h2>
          </div>
          <section className="panel">
            <div className="panel-heading">
              <h3>Environments</h3>
              <span>{detail.environments.length} configured</span>
            </div>
            {detail.environments.map((environment) => (
              <form
                key={environment.id}
                className="environment-form"
                onSubmit={(event) => {
                  event.preventDefault();
                  const form = new FormData(event.currentTarget);
                  void api(`/api/v1/projects/${selected}/environments/${environment.id}`, token, {
                    method: "PATCH",
                    body: JSON.stringify({
                      target: {
                        type: form.get("type"),
                        containerName: form.get("containerName") || null,
                        network: form.get("network") || null,
                        hostPort: Number(form.get("hostPort")),
                        containerPort: Number(form.get("containerPort")),
                        healthPath: form.get("healthPath") || "/health",
                        healthTimeoutSeconds: Number(form.get("healthTimeoutSeconds")),
                      },
                    }),
                  })
                    .then(() => open(selected))
                    .catch((error: Error) => onError(error.message));
                }}
              >
                <p className="environment-title">
                  {environment.name} <span>{environment.targetType}</span>
                </p>
                <div className="form-grid">
                  <label className="field-label">
                    Target type
                    <select defaultValue={environment.targetType} name="type">
                      <option value="noop">noop</option>
                      <option value="docker">docker</option>
                    </select>
                  </label>
                  <label className="field-label">
                    Container name
                    <input
                      defaultValue={environment.containerName ?? ""}
                      name="containerName"
                      placeholder="Container name"
                    />
                  </label>
                  <label className="field-label">
                    Network
                    <input defaultValue={environment.network ?? ""} name="network" placeholder="Network" />
                  </label>
                  <label className="field-label">
                    Host port
                    <input defaultValue={environment.hostPort || 8102} name="hostPort" inputMode="numeric" />
                  </label>
                  <label className="field-label">
                    Container port
                    <input defaultValue={environment.containerPort || 8080} name="containerPort" inputMode="numeric" />
                  </label>
                  <label className="field-label">
                    Health path
                    <input defaultValue={environment.healthPath ?? "/health"} name="healthPath" />
                  </label>
                  <label className="field-label">
                    Health timeout (seconds)
                    <input
                      defaultValue={environment.healthTimeoutSeconds || 60}
                      name="healthTimeoutSeconds"
                      inputMode="numeric"
                    />
                  </label>
                </div>
                <div className="action-row">
                  <button className="primary-button" type="submit">
                    Save target
                  </button>
                  <button
                    className="secondary-button"
                    type="button"
                    onClick={() =>
                      void api<{ containers: Array<{ name: string; state: string; image: string }> }>(
                        `/api/v1/projects/${selected}/environments/${environment.id}/runtime`,
                        token,
                      )
                        .then((body) => setRuntime(JSON.stringify(body)))
                        .catch((error: Error) => onError(error.message))
                    }
                  >
                    Runtime
                  </button>
                </div>
              </form>
            ))}
            {runtime && <pre className="runtime-output">{runtime}</pre>}
          </section>
          <section className="panel">
            <div className="panel-heading">
              <h3>API keys</h3>
              <span>{keys.length} keys</span>
            </div>
            <div className="action-row">
              <label className="field-label grow">
                Key name
                <input value={keyName} onChange={(event) => setKeyName(event.target.value)} />
              </label>
              <button
                className="primary-button self-end"
                type="button"
                onClick={() => void createKey().catch((error: Error) => onError(error.message))}
              >
                Create
              </button>
            </div>
            {plaintext && <p className="key-notice">Copy this key now: {plaintext}</p>}
            <ul className="item-list key-list">
              {keys.map((key) => (
                <li key={key.id} className="list-row">
                  <span>
                    {key.name} · {key.keyPrefix} ·{" "}
                    {key.revokedAt ? "revoked" : key.lastUsedAt ? `used ${key.lastUsedAt}` : "never used"}
                  </span>
                  {!key.revokedAt && (
                    <button
                      className="danger-button"
                      type="button"
                      onClick={() => void revokeKey(key.id).catch((error: Error) => onError(error.message))}
                    >
                      Revoke
                    </button>
                  )}
                </li>
              ))}
            </ul>
          </section>
          <section className="panel">
            <div className="panel-heading">
              <h3>Artifacts</h3>
              <span>{artifacts.length} available</span>
            </div>
            {artifacts.map((artifact) => (
              <article key={artifact.id} className="artifact-row">
                <p>
                  <strong>{artifact.version}</strong>
                  <span> · {artifact.branch}</span>
                </p>
                <div className="action-row">
                  {detail.environments.map((environment) => (
                    <button
                      key={environment.id}
                      className="secondary-button"
                      type="button"
                      disabled={pendingDeployments.includes(`${artifact.id}:${environment.id}`)}
                      onClick={() => void onDeploy(artifact, environment)}
                    >
                      {pendingDeployments.includes(`${artifact.id}:${environment.id}`) ? (
                        <>
                          <span className="button-spinner" aria-hidden="true" /> Requesting {environment.name}…
                        </>
                      ) : (
                        `Deploy ${environment.name}`
                      )}
                    </button>
                  ))}
                </div>
              </article>
            ))}
          </section>
        </div>
      )}
      {!selected && (
        <div className="panel selection-placeholder">
          <p>Select a project to view environments, keys, and artifacts.</p>
        </div>
      )}
    </div>
  );
}

type DeploymentListItem = {
  id: string;
  status: string;
  approvalsReceived: number;
  approvalsRequired: number;
  requestedAt?: string | null;
  failureReason?: string;
};

function requestTime(value: string | null | undefined): number | null {
  if (!value) return null;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : null;
}

function Deployments({
  token,
  onError,
  revision,
  polling,
}: {
  token: string;
  onError: (value: string | null) => void;
  revision: number;
  polling: boolean;
}) {
  const [items, setItems] = useState<DeploymentListItem[]>([]);
  const [events, setEvents] = useState<Array<{ sequence: number; type: string; message: string }>>([]);
  const [rules, setRules] = useState<Array<{ type: string; passed: boolean; message: string }>>([]);
  const [findings, setFindings] = useState<
    Array<{ id: string; severity: string; title: string; cve?: string; packageName?: string; status: string }>
  >([]);
  const [decision, setDecision] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [canRollback, setCanRollback] = useState(false);
  const [aiSummary, setAiSummary] = useState<{ deploymentId: string; text: string } | null>(null);
  const [summaryPendingId, setSummaryPendingId] = useState<string | null>(null);
  const [summaryError, setSummaryError] = useState<{ deploymentId: string; message: string } | null>(null);
  const [findingExplanation, setFindingExplanation] = useState<{ findingId: string; text: string } | null>(null);
  const [explanationPendingId, setExplanationPendingId] = useState<string | null>(null);
  const [explanationError, setExplanationError] = useState<{ findingId: string; message: string } | null>(null);

  // Promise-chained so state is only set in callbacks; this keeps polling effects free of synchronous setState.
  function load() {
    return api<DeploymentListItem[]>("/api/v1/deployments", token).then(setItems);
  }

  // Effect events read the latest token/selection without re-subscribing the polling effects.
  const pollItems = useEffectEvent((reportErrors: boolean) => {
    load().catch((error: Error) => {
      if (reportErrors) onError(error.message);
    });
  });
  const reloadForRevision = useEffectEvent(() => {
    void load().catch(() => undefined);
    if (selected)
      void open(selected).catch(() => {
        setSelected(null);
        setEvents([]);
        setRules([]);
        setDecision(null);
      });
  });
  const pollSelected = useEffectEvent((id: string) => {
    void open(id).catch(() => undefined);
  });

  useEffect(() => {
    pollItems(true);
  }, [token]);

  // While live, hub DeploymentChanged hints bump `revision`; each one reloads the list and the open timeline once.
  useEffect(() => {
    reloadForRevision();
  }, [revision]);

  // Fallback timers run only while the hub is disconnected or reconnecting.
  useEffect(() => {
    if (!polling) return;
    const timer = setInterval(() => pollItems(false), pollingFallbackMs);
    return () => clearInterval(timer);
  }, [token, polling]);

  useEffect(() => {
    if (!selected || !polling) return;
    const timer = setInterval(() => pollSelected(selected), pollingFallbackMs);
    return () => clearInterval(timer);
  }, [selected, token, polling]);

  function open(id: string) {
    return api<{ artifactId: string; status: string; previousArtifactId?: string }>(
      `/api/v1/deployments/${id}`,
      token,
    ).then(async (deployment) => {
      setSelected(id);
      setCanRollback(deployment.status === "Succeeded" && Boolean(deployment.previousArtifactId));
      const [timeline, evaluation, found] = await Promise.all([
        api<Array<{ sequence: number; type: string; message: string }>>(`/api/v1/deployments/${id}/events`, token),
        api<{ decision: string; ruleResults: Array<{ type: string; passed: boolean; message: string }> }>(
          `/api/v1/deployments/${id}/evaluation`,
          token,
        ).catch(() => null),
        api<Array<{ id: string; severity: string; title: string; cve?: string; packageName?: string; status: string }>>(
          `/api/v1/artifacts/${deployment.artifactId}/findings`,
          token,
        ).catch(() => []),
      ]);
      setEvents(timeline);
      setDecision(evaluation?.decision ?? null);
      setFindings(found);
      setRules(
        (evaluation?.ruleResults ?? []).map((rule) => {
          const row = rule as {
            type?: string;
            Type?: string;
            passed?: boolean;
            Passed?: boolean;
            message?: string;
            Message?: string;
          };
          return {
            type: row.type ?? row.Type ?? "rule",
            passed: row.passed ?? row.Passed ?? false,
            message: row.message ?? row.Message ?? "",
          };
        }),
      );
    });
  }

  async function summarizeDeployment(id: string) {
    setSummaryPendingId(id);
    setSummaryError(null);
    try {
      const result = await api<{ summary: string }>(`/api/v1/deployments/${id}/ai-summary`, token, {
        method: "POST",
        body: JSON.stringify({}),
      });
      setAiSummary({ deploymentId: id, text: result.summary });
    } catch (error) {
      setSummaryError({
        deploymentId: id,
        message: error instanceof Error ? error.message : "Could not generate a summary.",
      });
    } finally {
      setSummaryPendingId((current) => (current === id ? null : current));
    }
  }

  async function explainFinding(id: string) {
    setExplanationPendingId(id);
    setExplanationError(null);
    try {
      const result = await api<{ explanation: string }>(`/api/v1/findings/${id}/ai-explain`, token, {
        method: "POST",
        body: JSON.stringify({}),
      });
      setFindingExplanation({ findingId: id, text: result.explanation });
    } catch (error) {
      setExplanationError({
        findingId: id,
        message: error instanceof Error ? error.message : "Could not explain this finding.",
      });
    } finally {
      setExplanationPendingId((current) => (current === id ? null : current));
    }
  }

  // Keep the API's order for equal or missing dates; never present an undated row as the latest.
  const recentItems = items
    .map((item, index) => ({ item, index, timestamp: requestTime(item.requestedAt) }))
    .sort((a, b) => (b.timestamp ?? -Infinity) - (a.timestamp ?? -Infinity) || a.index - b.index);
  const selectedDeployment = items.find((item) => item.id === selected);

  return (
    <div className="split-layout">
      <section className="panel">
        <div className="panel-heading">
          <h2>Recent deployments</h2>
          <span>{items.length} total</span>
        </div>
        <ul className="item-list">
          {recentItems.map(({ item, timestamp }, index) => (
            <li key={item.id}>
              <button
                className={`list-button deployment-list-button ${selected === item.id ? "is-selected" : ""}`}
                type="button"
                onClick={() => void open(item.id).catch((error: Error) => onError(error.message))}
              >
                <span className="deployment-list-heading">
                  <strong>{item.status}</strong>
                  {index === 0 && timestamp !== null && <span className="latest-marker">Latest</span>}
                </span>
                <span className="deployment-list-time">
                  Requested:{" "}
                  {timestamp !== null ? (
                    <time dateTime={item.requestedAt ?? undefined}>
                      {new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(
                        timestamp,
                      )}
                    </time>
                  ) : (
                    "time unavailable"
                  )}
                </span>
                <span>
                  Approvals {item.approvalsReceived}/{item.approvalsRequired}
                </span>
                {item.failureReason && <span className="warning-text">{item.failureReason}</span>}
              </button>
            </li>
          ))}
        </ul>
        {items.length === 0 && <p className="empty-state">No deployments to show yet.</p>}
      </section>
      <section className="panel">
        <div className="panel-heading">
          <h2>Deployment details</h2>
        </div>
        <div className="detail-stack">
          {selected && canRollback && (
            <button
              className="secondary-button"
              type="button"
              onClick={() =>
                void api(`/api/v1/deployments/${selected}/rollback`, token, { method: "POST" })
                  .then(() => load())
                  .catch((error: Error) => onError(error.message))
              }
            >
              Rollback
            </button>
          )}
          {selected && selectedDeployment?.status === "AwaitingApproval" && (
            <div className="ai-action-group">
              <button
                className="secondary-button"
                type="button"
                disabled={summaryPendingId === selected}
                aria-busy={summaryPendingId === selected}
                onClick={() => void summarizeDeployment(selected)}
              >
                {summaryPendingId === selected ? (
                  <>
                    <span className="button-spinner" aria-hidden="true" />
                    Generating summary…
                  </>
                ) : (
                  "Generate AI summary"
                )}
              </button>
              {summaryError?.deploymentId === selected && (
                <p className="ai-error" role="alert">
                  {summaryError.message}
                </p>
              )}
              {aiSummary?.deploymentId === selected && (
                <div className="ai-result" aria-live="polite">
                  <strong>Deployment summary</strong>
                  <p>{aiSummary.text}</p>
                  <small>AI-generated — verify before acting.</small>
                </div>
              )}
            </div>
          )}
          {decision && (
            <p className="detail-row">
              Policy decision: <strong>{decision}</strong>
            </p>
          )}
          {rules.length > 0 && <h3 className="subheading">Policy checks</h3>}
          <ul className="item-list">
            {rules.map((rule) => (
              <li key={`${rule.type}-${rule.message}`} className="detail-row">
                {rule.passed ? "Pass" : "Fail"} · {rule.type}: {rule.message}
              </li>
            ))}
          </ul>
          {findings.length > 0 && <h3 className="subheading">Findings</h3>}
          <ul className="item-list">
            {findings.map((finding) => (
              <li key={finding.id} className="detail-row finding-ai-row">
                <span>
                  {finding.severity} · {finding.title}
                  {finding.cve ? ` · ${finding.cve}` : ""}
                  {finding.packageName ? ` · ${finding.packageName}` : ""} · {finding.status}
                </span>
                <button
                  className="secondary-button"
                  type="button"
                  disabled={explanationPendingId === finding.id}
                  aria-busy={explanationPendingId === finding.id}
                  onClick={() => void explainFinding(finding.id)}
                >
                  {explanationPendingId === finding.id ? (
                    <>
                      <span className="button-spinner" aria-hidden="true" />
                      Explaining…
                    </>
                  ) : (
                    "Explain with AI"
                  )}
                </button>
                {explanationError?.findingId === finding.id && (
                  <p className="ai-error" role="alert">
                    {explanationError.message}
                  </p>
                )}
                {findingExplanation?.findingId === finding.id && (
                  <div className="ai-result" aria-live="polite">
                    <strong>Finding explanation</strong>
                    <p>{findingExplanation.text}</p>
                    <small>AI-generated — verify before acting.</small>
                  </div>
                )}
              </li>
            ))}
          </ul>
          {events.length > 0 && <h3 className="subheading">Timeline</h3>}
          <ol className="item-list">
            {events.map((item) => (
              <li key={item.sequence} className="detail-row">
                {item.type}: {item.message}
              </li>
            ))}
          </ol>
          {!selected && <p className="empty-state">Select a deployment to inspect its timeline and policy checks.</p>}
        </div>
      </section>
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
    <ul className="panel item-list">
      {items.map((item) => (
        <li key={item.id} className="list-row">
          <span className="break-id">{item.id}</span>
          <span className="action-row">
            <button
              className="primary-button"
              type="button"
              onClick={() => void decide(item.id, "Approved").catch((error: Error) => onError(error.message))}
            >
              Approve
            </button>
            <button
              className="danger-button"
              type="button"
              onClick={() => void decide(item.id, "Rejected").catch((error: Error) => onError(error.message))}
            >
              Reject
            </button>
          </span>
        </li>
      ))}
      {items.length === 0 && <li className="empty-state">No requests awaiting approval.</li>}
    </ul>
  );
}

function Policies({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  type PolicyRule = { type: string; effect: string; order: number; parameters: object };
  type PolicyItem = {
    id: string;
    name: string;
    description: string | null;
    version: number;
    scope: string;
    scopeId: string | null;
    isEnabled: boolean;
    appliesToTiers: string[];
    rules: PolicyRule[];
  };
  const [items, setItems] = useState<PolicyItem[]>([]);
  const [catalogue, setCatalogue] = useState<Array<{ type: string; effect: string; parameters: object }>>([]);
  const [choice, setChoice] = useState("");
  const [parameterDrafts, setParameterDrafts] = useState<Record<string, string>>({});
  const [savingPolicyId, setSavingPolicyId] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([
      api<PolicyItem[]>("/api/v1/policies", token),
      api<Array<{ type: string; effect: string; parameters: object }>>("/api/v1/policies/rule-types", token),
    ])
      .then(([policies, types]) => {
        setItems(policies);
        setCatalogue(types);
        setChoice(types[0]?.type ?? "");
      })
      .catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function saveRules(policy: PolicyItem, rules: PolicyRule[], clearDraftKey?: string) {
    setSavingPolicyId(policy.id);
    try {
      await api(`/api/v1/policies/${policy.id}`, token, {
        method: "PUT",
        body: JSON.stringify({
          name: policy.name,
          description: policy.description,
          scope: policy.scope,
          scopeId: policy.scopeId,
          appliesToTiers: policy.appliesToTiers,
          rules,
        }),
      });
      setItems(await api<PolicyItem[]>("/api/v1/policies", token));
      if (clearDraftKey) {
        setParameterDrafts((current) => {
          const next = { ...current };
          delete next[clearDraftKey];
          return next;
        });
      }
      onError(null);
    } finally {
      setSavingPolicyId(null);
    }
  }

  async function addRule(policy: PolicyItem) {
    const template = catalogue.find((item) => item.type === choice);
    if (!template) {
      return;
    }
    const rules = [
      ...policy.rules,
      { type: template.type, effect: template.effect, order: policy.rules.length, parameters: template.parameters },
    ];
    await saveRules(policy, rules);
  }

  async function saveRuleParameters(policy: PolicyItem, rule: PolicyRule) {
    const key = `${policy.id}:${rule.order}`;
    const source = parameterDrafts[key] ?? JSON.stringify(rule.parameters ?? {}, null, 2);
    let parameters: object;
    try {
      const parsed: unknown = JSON.parse(source);
      if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
        throw new Error("Rule parameters must be a JSON object.");
      }
      parameters = parsed;
    } catch (error) {
      if (error instanceof Error && error.message === "Rule parameters must be a JSON object.") {
        throw error;
      }
      throw new Error(`${rule.type} parameters must be valid JSON.`);
    }

    const rules = policy.rules.map((current) => (current.order === rule.order ? { ...current, parameters } : current));
    await saveRules(policy, rules, key);
  }

  return (
    <div className="detail-stack">
      <div className="panel">
        <label className="field-label policy-select">
          Rule to add
          <select value={choice} onChange={(event) => setChoice(event.target.value)}>
            {catalogue.map((item) => (
              <option key={item.type} value={item.type}>
                {item.type}
              </option>
            ))}
          </select>
        </label>
      </div>
      <ul className="policy-list">
        {items.map((policy) => (
          <li key={policy.id} className="panel">
            <div className="panel-heading policy-heading">
              <h2>
                {policy.name} <span>v{policy.version}</span>
              </h2>
              <button
                className="primary-button"
                type="button"
                disabled={savingPolicyId === policy.id}
                onClick={() => void addRule(policy).catch((error: Error) => onError(error.message))}
              >
                Add rule
              </button>
            </div>
            <ul className="item-list">
              {policy.rules.map((rule) => {
                const key = `${policy.id}:${rule.order}`;
                return (
                  <li className="policy-rule" key={`${rule.type}-${rule.order}`}>
                    <strong>
                      {rule.order}. {rule.type} ({rule.effect})
                    </strong>
                    <label className="field-label policy-rule-parameters">
                      {policy.name} {rule.type} parameters
                      <textarea
                        aria-label={`${policy.name} ${rule.type} parameters`}
                        spellCheck={false}
                        value={parameterDrafts[key] ?? JSON.stringify(rule.parameters ?? {}, null, 2)}
                        onChange={(event) =>
                          setParameterDrafts((current) => ({ ...current, [key]: event.target.value }))
                        }
                      />
                    </label>
                    <button
                      className="secondary-button"
                      type="button"
                      disabled={savingPolicyId === policy.id}
                      onClick={() =>
                        void saveRuleParameters(policy, rule).catch((error: Error) => onError(error.message))
                      }
                    >
                      Save {rule.type} parameters
                    </button>
                  </li>
                );
              })}
            </ul>
          </li>
        ))}
      </ul>
    </div>
  );
}

type AiRequestItem = {
  id: string;
  status: string;
  purpose: string;
  promptChars: number;
  responseChars: number;
  promptTokens: number | null;
  completionTokens: number | null;
  latencyMs: number;
  detectedSensitiveTypesJson: string;
  redactionsApplied: number;
  blockReason?: string | null;
  createdAt: string;
};
type AiPolicyItem = {
  id: string;
  name: string;
  scope: string;
  scopeId: string | null;
  allowedModelIds: string[];
  requestsPerMinute: number;
  requestsPerDay: number;
  maxPromptChars: number;
  sensitiveDataAction: string;
  blockedPatterns: string[];
  redactPii: boolean;
  storeContent: boolean;
  isEnabled: boolean;
  version: number;
};
type AiPolicyResponse = Omit<AiPolicyItem, "allowedModelIds" | "blockedPatterns"> & {
  allowedModelIdsJson: string;
  blockedPatternsJson: string;
};

function jsonStringArray(value: string): string[] {
  try {
    const parsed: unknown = JSON.parse(value);
    return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === "string") : [];
  } catch {
    return [];
  }
}

function normalizeAiPolicy(policy: AiPolicyResponse): AiPolicyItem {
  return {
    ...policy,
    allowedModelIds: jsonStringArray(policy.allowedModelIdsJson),
    blockedPatterns: jsonStringArray(policy.blockedPatternsJson),
  };
}

function AiGovernance({ token }: { token: string }) {
  const pageSize = 20;
  const [page, setPage] = useState(1);
  const [requests, setRequests] = useState<AiRequestItem[]>([]);
  const [requestTotal, setRequestTotal] = useState(0);
  const [requestLoading, setRequestLoading] = useState(true);
  const [requestError, setRequestError] = useState<string | null>(null);
  const [policies, setPolicies] = useState<AiPolicyItem[]>([]);
  const [selectedPolicyId, setSelectedPolicyId] = useState("");
  const [policyLoading, setPolicyLoading] = useState(true);
  const [policyError, setPolicyError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [policyMessage, setPolicyMessage] = useState<string | null>(null);

  // Loading/error state starts as "loading" and is reset by goToPage, so the effects only set state in callbacks.
  useEffect(() => {
    let active = true;
    api<{ total: number; items: AiRequestItem[] }>(`/api/v1/ai/requests?page=${page}&pageSize=${pageSize}`, token)
      .then((result) => {
        if (!active) return;
        setRequests(result.items);
        setRequestTotal(result.total);
      })
      .catch((error: Error) => {
        if (active) setRequestError(error.message);
      })
      .finally(() => {
        if (active) setRequestLoading(false);
      });
    return () => {
      active = false;
    };
  }, [page, token]);

  useEffect(() => {
    let active = true;
    api<AiPolicyResponse[]>("/api/v1/ai/policies", token)
      .then((result) => {
        if (!active) return;
        const loaded = result.map(normalizeAiPolicy);
        setPolicies(loaded);
        setSelectedPolicyId((current) =>
          loaded.some((item) => item.id === current) ? current : (loaded[0]?.id ?? ""),
        );
      })
      .catch((error: Error) => {
        if (active) setPolicyError(error.message);
      })
      .finally(() => {
        if (active) setPolicyLoading(false);
      });
    return () => {
      active = false;
    };
  }, [token]);

  function goToPage(next: number) {
    if (next === page) return;
    setRequestLoading(true);
    setRequestError(null);
    setPage(next);
  }

  const selectedPolicy = policies.find((item) => item.id === selectedPolicyId);
  function updatePolicy(patch: Partial<AiPolicyItem>) {
    if (!selectedPolicy) return;
    setPolicies((current) => current.map((item) => (item.id === selectedPolicy.id ? { ...item, ...patch } : item)));
    setPolicyMessage(null);
  }

  async function savePolicy() {
    if (!selectedPolicy) return;
    setSaving(true);
    setPolicyError(null);
    setPolicyMessage(null);
    try {
      await api("/api/v1/ai/policies", token, {
        method: "PUT",
        body: JSON.stringify({
          id: selectedPolicy.id,
          name: selectedPolicy.name,
          scope: selectedPolicy.scope,
          scopeId: selectedPolicy.scopeId,
          allowedModelIds: selectedPolicy.allowedModelIds,
          requestsPerMinute: selectedPolicy.requestsPerMinute,
          requestsPerDay: selectedPolicy.requestsPerDay,
          maxPromptChars: selectedPolicy.maxPromptChars,
          sensitiveDataAction: selectedPolicy.sensitiveDataAction,
          blockedPatterns: selectedPolicy.blockedPatterns,
          redactPii: selectedPolicy.redactPii,
          storeContent: selectedPolicy.storeContent,
          isEnabled: selectedPolicy.isEnabled,
        }),
      });
      const refreshed = (await api<AiPolicyResponse[]>("/api/v1/ai/policies", token)).map(normalizeAiPolicy);
      setPolicies(refreshed);
      setPolicyMessage("AI policy saved.");
    } catch (error) {
      setPolicyError(error instanceof Error ? error.message : "Could not save AI policy.");
    } finally {
      setSaving(false);
    }
  }

  const pageCount = Math.max(1, Math.ceil(requestTotal / pageSize));
  return (
    <div className="ai-governance">
      <section className="panel">
        <div className="panel-heading">
          <h2>AI request history</h2>
          <span>
            {requestTotal} total · page {page} of {pageCount}
          </span>
        </div>
        <p className="empty-state">
          Request previews are omitted here; sensitive-data detections, redactions, and outcome metadata are shown
          instead.
        </p>
        {requestError && (
          <p className="ai-error" role="alert">
            {requestError}
          </p>
        )}
        {requestLoading ? (
          <p className="empty-state" role="status">
            Loading AI requests…
          </p>
        ) : (
          <ul className="item-list ai-request-list">
            {requests.map((request) => (
              <li key={request.id} className="detail-row ai-request-row">
                <div className="ai-request-heading">
                  <strong>{request.purpose}</strong>
                  <span className={`ai-status ai-status-${request.status.toLowerCase()}`}>{request.status}</span>
                  <time dateTime={request.createdAt}>{new Date(request.createdAt).toLocaleString()}</time>
                </div>
                <span>
                  {request.promptChars} prompt chars · {request.responseChars} response chars · {request.latencyMs} ms
                </span>
                <span>
                  Tokens {request.promptTokens ?? "—"}/{request.completionTokens ?? "—"} · {request.redactionsApplied}{" "}
                  redactions
                </span>
                {request.blockReason && <span>Block reason: {request.blockReason}</span>}
                {!!jsonStringArray(request.detectedSensitiveTypesJson).length && (
                  <span>Detected: {jsonStringArray(request.detectedSensitiveTypesJson).join(", ")}</span>
                )}
              </li>
            ))}
            {!requests.length && (
              <li className="empty-state">
                {requestTotal ? "No requests on this page." : "No AI requests recorded yet."}
              </li>
            )}
          </ul>
        )}
        <div className="action-row ai-pagination">
          <button
            className="secondary-button"
            type="button"
            disabled={page <= 1 || requestLoading}
            onClick={() => goToPage(Math.max(1, page - 1))}
          >
            Previous
          </button>
          <button
            className="secondary-button"
            type="button"
            disabled={page >= pageCount || requestLoading}
            onClick={() => goToPage(Math.min(pageCount, page + 1))}
          >
            Next
          </button>
        </div>
      </section>

      <section className="panel">
        <div className="panel-heading">
          <h2>AI policy controls</h2>
        </div>
        <p className="empty-state">
          Global policies are editable by Admin; team policies are editable by Security. Allowed model IDs are preserved
          when saving.
        </p>
        {policyError && (
          <p className="ai-error" role="alert">
            {policyError}
          </p>
        )}
        {policyMessage && (
          <p className="policy-message" role="status">
            {policyMessage}
          </p>
        )}
        {policyLoading ? (
          <p className="empty-state" role="status">
            Loading AI policies…
          </p>
        ) : policies.length === 0 ? (
          <p className="empty-state">No AI policies are configured.</p>
        ) : (
          <>
            <label className="field-label" htmlFor="ai-policy-select">
              AI policy
            </label>
            <select
              id="ai-policy-select"
              value={selectedPolicyId}
              onChange={(event) => {
                setSelectedPolicyId(event.target.value);
                setPolicyMessage(null);
              }}
            >
              {policies.map((policy) => (
                <option key={policy.id} value={policy.id}>
                  {policy.scope} · {policy.name}
                </option>
              ))}
            </select>
            {selectedPolicy && (
              <div className="ai-policy-form">
                <p className="empty-state">
                  Version {selectedPolicy.version} · {selectedPolicy.allowedModelIds.length} allowed model(s)
                </p>
                <div className="form-grid">
                  <label className="field-label">
                    Requests per minute
                    <input
                      type="number"
                      min="1"
                      max="10000"
                      value={selectedPolicy.requestsPerMinute}
                      onChange={(event) => updatePolicy({ requestsPerMinute: Number(event.target.value) })}
                    />
                  </label>
                  <label className="field-label">
                    Requests per day
                    <input
                      type="number"
                      min="1"
                      max="100000"
                      value={selectedPolicy.requestsPerDay}
                      onChange={(event) => updatePolicy({ requestsPerDay: Number(event.target.value) })}
                    />
                  </label>
                  <label className="field-label">
                    Maximum prompt characters
                    <input
                      type="number"
                      min="1"
                      max="100000"
                      value={selectedPolicy.maxPromptChars}
                      onChange={(event) => updatePolicy({ maxPromptChars: Number(event.target.value) })}
                    />
                  </label>
                  <label className="field-label">
                    Sensitive-data action
                    <select
                      value={selectedPolicy.sensitiveDataAction}
                      onChange={(event) => updatePolicy({ sensitiveDataAction: event.target.value })}
                    >
                      <option value="Allow">Allow</option>
                      <option value="Redact">Redact</option>
                      <option value="Block">Block</option>
                    </select>
                  </label>
                </div>
                <label className="field-label">
                  Blocked patterns, one per line
                  <textarea
                    rows={4}
                    value={selectedPolicy.blockedPatterns.join("\n")}
                    onChange={(event) =>
                      updatePolicy({
                        blockedPatterns: event.target.value
                          .split("\n")
                          .map((item) => item.trim())
                          .filter(Boolean),
                      })
                    }
                  />
                </label>
                <div className="ai-policy-toggles">
                  <label>
                    <input
                      type="checkbox"
                      checked={selectedPolicy.redactPii}
                      onChange={(event) => updatePolicy({ redactPii: event.target.checked })}
                    />{" "}
                    Redact personal data
                  </label>
                  <label>
                    <input
                      type="checkbox"
                      checked={selectedPolicy.storeContent}
                      onChange={(event) => updatePolicy({ storeContent: event.target.checked })}
                    />{" "}
                    Store prompt and response content
                  </label>
                  <label>
                    <input
                      type="checkbox"
                      checked={selectedPolicy.isEnabled}
                      onChange={(event) => updatePolicy({ isEnabled: event.target.checked })}
                    />{" "}
                    Policy enabled
                  </label>
                </div>
                {selectedPolicy.storeContent && (
                  <p className="policy-warning" role="note">
                    Content storage retains full prompts and responses, which may contain sensitive information.
                  </p>
                )}
                <button className="primary-button" type="button" disabled={saving} onClick={() => void savePolicy()}>
                  {saving ? "Saving…" : "Save AI policy"}
                </button>
              </div>
            )}
          </>
        )}
      </section>
    </div>
  );
}

function Admin({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [users, setUsers] = useState<Array<{ id: string; email: string; displayName: string; roles: string[] }>>([]);
  const [teams, setTeams] = useState<Array<{ slug: string; name: string; myRole?: string | null }>>([]);
  const [webhooks, setWebhooks] = useState<Record<string, { url: string; configured: boolean; eventTypes: string[] }>>(
    {},
  );
  const [webhookUrls, setWebhookUrls] = useState<Record<string, string>>({});
  const [webhookSecrets, setWebhookSecrets] = useState<Record<string, string>>({});
  const [savingWebhook, setSavingWebhook] = useState<string | null>(null);
  const [webhookMessages, setWebhookMessages] = useState<Record<string, string>>({});
  const eventTypes = [
    "Requested",
    "PolicyEvaluated",
    "ApprovalRequested",
    "ApprovalReceived",
    "Approved",
    "Rejected",
    "Denied",
    "DeployStarted",
    "Succeeded",
    "Failed",
    "Cancelled",
    "ImagePulled",
    "ContainerStarted",
    "HealthCheckPassed",
    "HealthCheckFailed",
    "RolledBack",
  ];
  const [webhookEvents, setWebhookEvents] = useState<Record<string, string[]>>({});
  const [webhookDeliveries, setWebhookDeliveries] = useState<
    Record<
      string,
      Array<{
        eventId: string;
        eventType: string;
        occurredAt: string;
        status: string;
        attempts: number;
        maxAttempts: number;
        completedAt: string | null;
        lastError: string | null;
      }>
    >
  >({});
  const [jobs, setJobs] = useState<Array<{ id: string; type: string; status: string; lastError?: string }>>([]);
  const [queue, setQueue] = useState<{ pending: number; running: number; deadLettered: number } | null>(null);
  useEffect(() => {
    Promise.all([
      api<Array<{ id: string; email: string; displayName: string; roles: string[] }>>("/api/v1/admin/users", token),
      api<Array<{ slug: string; name: string; myRole?: string | null }>>("/api/v1/teams", token),
      api<Array<{ id: string; type: string; status: string; lastError?: string }>>(
        "/api/v1/admin/jobs?status=DeadLettered",
        token,
      ).catch(() => []),
      api<{ pending: number; running: number; deadLettered: number }>("/api/v1/admin/jobs/summary", token).catch(
        () => null,
      ),
    ])
      .then(([accounts, groups, dead, depth]) => {
        setUsers(accounts);
        setTeams(groups);
        void Promise.all(
          groups.map(async (team) => {
            try {
              const settings = await api<{ url: string | null; secretConfigured: boolean; eventTypes: string[] }>(
                `/api/v1/teams/${encodeURIComponent(team.slug)}/webhook`,
                token,
              );
              setWebhooks((current) => ({
                ...current,
                [team.slug]: {
                  url: settings.url ?? "",
                  configured: settings.secretConfigured,
                  eventTypes: settings.eventTypes,
                },
              }));
              setWebhookUrls((current) => ({ ...current, [team.slug]: settings.url ?? "" }));
              setWebhookEvents((current) => ({ ...current, [team.slug]: settings.eventTypes }));
              const deliveries = await api<(typeof webhookDeliveries)[string]>(
                `/api/v1/teams/${encodeURIComponent(team.slug)}/webhook/deliveries`,
                token,
              );
              setWebhookDeliveries((current) => ({ ...current, [team.slug]: deliveries }));
            } catch {
              setWebhookMessages((current) => ({
                ...current,
                [team.slug]: "Webhook settings are available to team owners and admins.",
              }));
            }
          }),
        );
        setJobs(dead);
        setQueue(depth);
      })
      .catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function saveWebhook(slug: string) {
    const url = webhookUrls[slug]?.trim() ?? "";
    const secret = webhookSecrets[slug] ?? "";
    if (!url || new TextEncoder().encode(secret).length < 32) {
      setWebhookMessages((current) => ({
        ...current,
        [slug]: "Enter an HTTPS endpoint and a signing secret of at least 32 bytes.",
      }));
      return;
    }
    setSavingWebhook(slug);
    setWebhookMessages((current) => ({ ...current, [slug]: "" }));
    try {
      const secretBase64 = btoa(String.fromCharCode(...new TextEncoder().encode(secret)));
      const settings = await api<{ url: string; secretConfigured: boolean; eventTypes: string[] }>(
        `/api/v1/teams/${encodeURIComponent(slug)}/webhook`,
        token,
        { method: "PUT", body: JSON.stringify({ url, secretBase64, eventTypes: webhookEvents[slug] ?? eventTypes }) },
      );
      setWebhooks((current) => ({
        ...current,
        [slug]: { url: settings.url, configured: settings.secretConfigured, eventTypes: settings.eventTypes },
      }));
      setWebhookSecrets((current) => ({ ...current, [slug]: "" }));
      setWebhookMessages((current) => ({
        ...current,
        [slug]: "Webhook settings saved. The signing secret is not shown again.",
      }));
    } catch (error) {
      setWebhookMessages((current) => ({
        ...current,
        [slug]: error instanceof Error ? error.message : "Could not save webhook settings.",
      }));
    } finally {
      setSavingWebhook(null);
    }
  }

  return (
    <div className="admin-grid">
      <section className="panel">
        <div className="panel-heading">
          <h2>Users</h2>
          <span>{users.length} total</span>
        </div>
        <ul className="item-list">
          {users.map((user) => (
            <li key={user.id} className="detail-row">
              {user.displayName} · {user.email} · {user.roles.join(", ")}
            </li>
          ))}
        </ul>
      </section>
      <section className="panel">
        <div className="panel-heading">
          <h2>Teams</h2>
          <span>{teams.length} total</span>
        </div>
        <ul className="item-list">
          {teams.map((team) => (
            <li key={team.slug} className="team-admin-row">
              <strong>{team.name}</strong>
              <span className="team-slug">{team.slug}</span>
              {team.myRole === "Owner" || !team.myRole ? (
                <div className="team-webhook-settings">
                  <div className="panel-heading">
                    <h3>Outbound webhook</h3>
                    <span>{webhooks[team.slug]?.configured ? "Configured" : "Not configured"}</span>
                  </div>
                  <p className="empty-state">
                    Send signed deployment events to a public HTTPS endpoint on port 443. Saving a new secret replaces
                    the existing one.
                  </p>
                  <label className="field-label" htmlFor={`webhook-url-${team.slug}`}>
                    HTTPS endpoint
                  </label>
                  <input
                    id={`webhook-url-${team.slug}`}
                    type="url"
                    placeholder="https://hooks.example.com/aegisops"
                    value={webhookUrls[team.slug] ?? ""}
                    onChange={(event) => setWebhookUrls((current) => ({ ...current, [team.slug]: event.target.value }))}
                  />
                  <label className="field-label" htmlFor={`webhook-secret-${team.slug}`}>
                    Signing secret (32–1024 bytes)
                  </label>
                  <input
                    id={`webhook-secret-${team.slug}`}
                    type="password"
                    autoComplete="new-password"
                    value={webhookSecrets[team.slug] ?? ""}
                    onChange={(event) =>
                      setWebhookSecrets((current) => ({ ...current, [team.slug]: event.target.value }))
                    }
                  />
                  <fieldset className="webhook-events">
                    <legend>Subscribed events</legend>
                    {eventTypes.map((eventType) => (
                      <label key={eventType}>
                        <input
                          type="checkbox"
                          checked={(webhookEvents[team.slug] ?? eventTypes).includes(eventType)}
                          onChange={(event) =>
                            setWebhookEvents((current) => {
                              const selected = current[team.slug] ?? eventTypes;
                              return {
                                ...current,
                                [team.slug]: event.target.checked
                                  ? [...selected, eventType]
                                  : selected.filter((item) => item !== eventType),
                              };
                            })
                          }
                        />
                        {eventType}
                      </label>
                    ))}
                  </fieldset>
                  {webhookMessages[team.slug] && (
                    <p className="webhook-message" role="status">
                      {webhookMessages[team.slug]}
                    </p>
                  )}
                  <button
                    className="primary-button"
                    type="button"
                    disabled={savingWebhook === team.slug}
                    onClick={() => void saveWebhook(team.slug)}
                  >
                    {savingWebhook === team.slug ? "Saving…" : "Save webhook"}
                  </button>
                  <div className="webhook-history">
                    <div className="panel-heading">
                      <h3>Recent delivery activity</h3>
                      <span>{webhookDeliveries[team.slug]?.length ?? 0} events</span>
                    </div>
                    {webhookDeliveries[team.slug]?.length ? (
                      <div className="webhook-metrics" aria-label="Recent delivery metrics">
                        <span>
                          <strong>
                            {webhookDeliveries[team.slug].filter((item) => item.status === "Succeeded").length}
                          </strong>{" "}
                          delivered
                        </span>
                        <span>
                          <strong>
                            {webhookDeliveries[team.slug].filter((item) => item.status === "DeadLettered").length}
                          </strong>{" "}
                          dead-lettered
                        </span>
                        <span>
                          <strong>
                            {
                              webhookDeliveries[team.slug].filter(
                                (item) => item.status === "Pending" || item.status === "Running",
                              ).length
                            }
                          </strong>{" "}
                          queued
                        </span>
                        <span>
                          <strong>
                            {webhookDeliveries[team.slug].filter((item) => item.status === "NotQueued").length}
                          </strong>{" "}
                          not queued
                        </span>
                        <span>
                          <strong>{webhookDeliveries[team.slug].reduce((sum, item) => sum + item.attempts, 0)}</strong>{" "}
                          attempts
                        </span>
                      </div>
                    ) : null}
                    {!webhookDeliveries[team.slug]?.length ? (
                      <p className="empty-state">No recent deployment events.</p>
                    ) : (
                      <ul className="item-list">
                        {webhookDeliveries[team.slug].slice(0, 10).map((delivery) => (
                          <li className="webhook-delivery" key={delivery.eventId}>
                            <span>
                              <strong>{delivery.eventType}</strong>
                              <small>{new Date(delivery.occurredAt).toLocaleString()}</small>
                            </span>
                            <span className={`delivery-status delivery-${delivery.status.toLowerCase()}`}>
                              {delivery.status}
                              {delivery.maxAttempts > 0
                                ? ` · ${delivery.attempts}/${delivery.maxAttempts} attempts`
                                : ""}
                            </span>
                            {delivery.lastError && <small>{delivery.lastError}</small>}
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                </div>
              ) : (
                <p className="empty-state">Webhook settings are visible to team owners and admins only.</p>
              )}
            </li>
          ))}
        </ul>
      </section>
      <section className="panel jobs-panel">
        <div className="panel-heading">
          <h2>Jobs</h2>
        </div>
        {queue && (
          <p className="queue-summary">
            Pending {queue.pending} · running {queue.running} · dead-letter {queue.deadLettered}
          </p>
        )}
        <ul className="item-list">
          {jobs.map((job) => (
            <li key={job.id} className="list-row">
              <span>
                {job.type} · {job.status}
                {job.lastError ? ` · ${job.lastError}` : ""}
              </span>
              <button
                className="secondary-button"
                type="button"
                onClick={() =>
                  void api(`/api/v1/admin/jobs/${job.id}/retry`, token, { method: "POST" })
                    .then(() => api<typeof jobs>("/api/v1/admin/jobs?status=DeadLettered", token).then(setJobs))
                    .catch((error: Error) => onError(error.message))
                }
              >
                Retry
              </button>
            </li>
          ))}
        </ul>
      </section>
    </div>
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
    <ul className="panel item-list">
      {items.map((item) => (
        <li key={item.id} className="audit-row">
          <time>{item.timestamp}</time>
          <span>{item.actorDisplay}</span>
          <strong>{item.action}</strong>
          <span>{item.outcome}</span>
        </li>
      ))}
      {items.length === 0 && <li className="empty-state">No audit activity to show yet.</li>}
    </ul>
  );
}
