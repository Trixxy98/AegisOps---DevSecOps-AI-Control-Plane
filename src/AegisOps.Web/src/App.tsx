import { useEffect, useState } from "react";
import { connectDeploymentUpdates } from "./deploymentUpdates";

type View = "dashboard" | "projects" | "deployments" | "approvals" | "policies" | "audit" | "admin";

const navigation: Array<{ id: View; label: string; description: string }> = [
  { id: "dashboard", label: "Overview", description: "Deployment activity at a glance" },
  { id: "projects", label: "Projects", description: "Environments, artifacts, and access" },
  { id: "deployments", label: "Deployments", description: "Track releases and review outcomes" },
  { id: "approvals", label: "Approvals", description: "Review requests awaiting a decision" },
  { id: "policies", label: "Policies", description: "Manage deployment guardrails" },
  { id: "audit", label: "Audit log", description: "A record of platform activity" },
  { id: "admin", label: "Administration", description: "People, teams, and background jobs" },
];

async function api<T>(path: string, token: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
      ...(init?.headers ?? {}),
    },
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.title ?? response.statusText);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return response.json() as Promise<T>;
}

export function App() {
  const [token, setToken] = useState<string | null>(null);
  const [view, setView] = useState<View>("dashboard");
  const [error, setError] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);

  useEffect(() => token ? connectDeploymentUpdates(token, () => setRevision((value) => value + 1)) : undefined, [token]);

  if (!token) {
    return <Login onToken={setToken} />;
  }

  return (
    <main className="app-shell">
      <aside className="sidebar">
        <div className="brand"><span className="brand-mark" aria-hidden="true">A</span><div><strong>AegisOps</strong><span>CONTROL PLANE</span></div></div>
        <div className="sidebar-label">WORKSPACE</div>
        <nav className="navigation" aria-label="Main navigation">
          {navigation.map((item) => (
            <button key={item.id} className={`nav-item ${view === item.id ? "is-active" : ""}`} aria-current={view === item.id ? "page" : undefined} onClick={() => { setView(item.id); setError(null); }} type="button">{item.label}</button>
          ))}
        </nav>
        <div className="sidebar-footer"><span className="status-dot" /> System workspace</div>
      </aside>
      <div className="main-column">
        <header className="topbar"><span className="topbar-label">Workspace <span>/</span> {navigation.find((item) => item.id === view)?.label}</span><button className="logout-button" type="button" onClick={() => setToken(null)}>Sign out</button></header>
        <div className="mobile-navigation"><nav className="navigation" aria-label="Main navigation">
          {navigation.map((item) => <button key={item.id} className={`nav-item ${view === item.id ? "is-active" : ""}`} aria-current={view === item.id ? "page" : undefined} onClick={() => { setView(item.id); setError(null); }} type="button">{item.label}</button>)}
        </nav></div>
        <section className="content">
        <div className="page-heading"><div><p className="eyebrow">AEGISOPS / WORKSPACE</p><h1>{navigation.find((item) => item.id === view)?.label}</h1><p className="page-description">{navigation.find((item) => item.id === view)?.description}</p></div><span className="heading-badge"><span className="status-dot" /> Live workspace</span></div>
        {error && <p role="alert" className="error-banner">{error}</p>}
         {view === "dashboard" && <Dashboard token={token} onError={setError} revision={revision} />}
        {view === "projects" && <Projects token={token} onError={setError} />}
         {view === "deployments" && <Deployments token={token} onError={setError} revision={revision} />}
         {view === "approvals" && <Approvals token={token} onError={setError} revision={revision} />}
        {view === "policies" && <Policies token={token} onError={setError} />}
        {view === "audit" && <Audit token={token} onError={setError} />}
        {view === "admin" && <Admin token={token} onError={setError} />}
        </section>
      </div>
    </main>
  );
}

function Login({ onToken }: { onToken: (token: string) => void }) {
  const [email, setEmail] = useState("admin@aegisops.local");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  return (
    <main className="login-page">
      <form className="login-card" onSubmit={async (event) => {
        event.preventDefault();
        const response = await fetch("/api/v1/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email, password }) });
        const body = await response.json();
        if (!response.ok) {
          setError(body.title ?? "Login failed");
          return;
        }
        onToken(body.accessToken);
      }}>
        <div className="brand"><span className="brand-mark" aria-hidden="true">A</span><div><strong>AegisOps</strong><span>CONTROL PLANE</span></div></div>
        <div className="login-heading"><p className="eyebrow">WELCOME BACK</p><h1>Sign in to your workspace</h1><p>Manage deployments securely from one place.</p></div>
        {error && <p role="alert" className="error-banner">{error}</p>}
        <label className="field-label" htmlFor="login-email">Email address</label>
        <input id="login-email" autoComplete="username" type="email" value={email} onChange={(event) => setEmail(event.target.value)} />
        <label className="field-label" htmlFor="login-password">Password</label>
        <input id="login-password" autoComplete="current-password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
        <button className="primary-button login-submit" type="submit">Continue</button>
      </form>
    </main>
  );
}

function Dashboard({ token, onError, revision }: { token: string; onError: (value: string | null) => void; revision: number }) {
  const [counts, setCounts] = useState<Record<string, number>>({});
  useEffect(() => {
    api<Array<{ status: string }>>("/api/v1/deployments", token)
      .then((items) => {
        const next: Record<string, number> = {};
        for (const item of items) {
          next[item.status] = (next[item.status] ?? 0) + 1;
        }
        setCounts(next);
      })
      .catch((error: Error) => onError(error.message));
  }, [token, onError, revision]);

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

function Projects({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [projects, setProjects] = useState<Array<{ slug: string; name: string }>>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<{ environments: Array<{ id: string; name: string; tier: string; targetType: string; containerName?: string; network?: string; hostPort: number; containerPort: number; healthPath?: string; healthTimeoutSeconds: number }> } | null>(null);
  const [runtime, setRuntime] = useState<string | null>(null);
  const [artifacts, setArtifacts] = useState<Array<{ id: string; version: string; branch: string }>>([]);
  const [keys, setKeys] = useState<Array<{ id: string; name: string; keyPrefix: string; lastUsedAt?: string; revokedAt?: string }>>([]);
  const [keyName, setKeyName] = useState("ci");
  const [plaintext, setPlaintext] = useState<string | null>(null);

  useEffect(() => {
    api<Array<{ slug: string; name: string }>>("/api/v1/projects", token).then(setProjects).catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function open(slug: string) {
    setSelected(slug);
    const [project, items, apiKeys] = await Promise.all([
      api<{ environments: Array<{ id: string; name: string; tier: string; targetType: string; containerName?: string; network?: string; hostPort: number; containerPort: number; healthPath?: string; healthTimeoutSeconds: number }> }>(`/api/v1/projects/${slug}`, token),
      api<Array<{ id: string; version: string; branch: string }>>(`/api/v1/projects/${slug}/artifacts`, token),
      api<Array<{ id: string; name: string; keyPrefix: string; lastUsedAt?: string; revokedAt?: string }>>(`/api/v1/projects/${slug}/api-keys`, token).catch(() => []),
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
      <section className="panel"><div className="panel-heading"><h2>Projects</h2><span>{projects.length} total</span></div><ul className="item-list">
        {projects.map((project) => (
          <li key={project.slug}><button className={`list-button ${selected === project.slug ? "is-selected" : ""}`} type="button" onClick={() => void open(project.slug)}><strong>{project.name}</strong><span>{project.slug}</span></button></li>
        ))}
      </ul>{projects.length === 0 && <p className="empty-state">No projects available.</p>}</section>
      {selected && detail && (
        <div className="detail-stack">
          <div className="detail-heading"><p className="eyebrow">PROJECT DETAILS</p><h2>{selected}</h2></div>
          <section className="panel"><div className="panel-heading"><h3>Environments</h3><span>{detail.environments.length} configured</span></div>
            {detail.environments.map((environment) => (
              <form key={environment.id} className="environment-form" onSubmit={(event) => {
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
                }).then(() => open(selected)).catch((error: Error) => onError(error.message));
              }}>
                <p className="environment-title">{environment.name} <span>{environment.targetType}</span></p>
                <div className="form-grid">
                  <label className="field-label">Target type<select defaultValue={environment.targetType} name="type">
                    <option value="noop">noop</option>
                    <option value="docker">docker</option>
                  </select></label>
                  <label className="field-label">Container name<input defaultValue={environment.containerName ?? ""} name="containerName" placeholder="Container name" /></label>
                  <label className="field-label">Network<input defaultValue={environment.network ?? ""} name="network" placeholder="Network" /></label>
                  <label className="field-label">Host port<input defaultValue={environment.hostPort || 8102} name="hostPort" inputMode="numeric" /></label>
                  <label className="field-label">Container port<input defaultValue={environment.containerPort || 8080} name="containerPort" inputMode="numeric" /></label>
                  <label className="field-label">Health path<input defaultValue={environment.healthPath ?? "/health"} name="healthPath" /></label>
                  <label className="field-label">Health timeout (seconds)<input defaultValue={environment.healthTimeoutSeconds || 60} name="healthTimeoutSeconds" inputMode="numeric" /></label>
                </div>
                <div className="action-row">
                  <button className="primary-button" type="submit">Save target</button>
                  <button className="secondary-button" type="button" onClick={() => void api<{ containers: Array<{ name: string; state: string; image: string }> }>(`/api/v1/projects/${selected}/environments/${environment.id}/runtime`, token).then((body) => setRuntime(JSON.stringify(body))).catch((error: Error) => onError(error.message))}>Runtime</button>
                </div>
              </form>
            ))}
            {runtime && <pre className="runtime-output">{runtime}</pre>}
          </section>
          <section className="panel"><div className="panel-heading"><h3>API keys</h3><span>{keys.length} keys</span></div>
            <div className="action-row">
              <label className="field-label grow">Key name<input value={keyName} onChange={(event) => setKeyName(event.target.value)} /></label>
              <button className="primary-button self-end" type="button" onClick={() => void createKey().catch((error: Error) => onError(error.message))}>Create</button>
            </div>
            {plaintext && <p className="key-notice">Copy this key now: {plaintext}</p>}
            <ul className="item-list key-list">
              {keys.map((key) => (
                <li key={key.id} className="list-row">
                  <span>{key.name} · {key.keyPrefix} · {key.revokedAt ? "revoked" : key.lastUsedAt ? `used ${key.lastUsedAt}` : "never used"}</span>
                  {!key.revokedAt && <button className="danger-button" type="button" onClick={() => void revokeKey(key.id).catch((error: Error) => onError(error.message))}>Revoke</button>}
                </li>
              ))}
            </ul>
          </section>
          <section className="panel"><div className="panel-heading"><h3>Artifacts</h3><span>{artifacts.length} available</span></div>{artifacts.map((artifact) => (
            <article key={artifact.id} className="artifact-row">
              <p><strong>{artifact.version}</strong><span> · {artifact.branch}</span></p>
              <div className="action-row">
                {detail.environments.map((environment) => (
                  <button key={environment.id} className="secondary-button" type="button" onClick={() => void api("/api/v1/deployments", token, { method: "POST", body: JSON.stringify({ artifactId: artifact.id, environmentId: environment.id, reason: "UI request" }) }).then(() => onError(null)).catch((error: Error) => onError(error.message))}>
                    Deploy {environment.name}
                  </button>
                ))}
              </div>
            </article>
          ))}</section>
        </div>
      )}
      {!selected && <div className="panel selection-placeholder"><p>Select a project to view environments, keys, and artifacts.</p></div>}
    </div>
  );
}

function Deployments({ token, onError, revision }: { token: string; onError: (value: string | null) => void; revision: number }) {
  const [items, setItems] = useState<Array<{ id: string; status: string; approvalsReceived: number; approvalsRequired: number; failureReason?: string }>>([]);
  const [events, setEvents] = useState<Array<{ sequence: number; type: string; message: string }>>([]);
  const [rules, setRules] = useState<Array<{ type: string; passed: boolean; message: string }>>([]);
  const [findings, setFindings] = useState<Array<{ id: string; severity: string; title: string; cve?: string; packageName?: string; status: string }>>([]);
  const [decision, setDecision] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [canRollback, setCanRollback] = useState(false);

  async function load() {
    setItems(await api("/api/v1/deployments", token));
  }

  useEffect(() => {
    load().catch((error: Error) => onError(error.message));
    const timer = setInterval(() => void load().catch(() => undefined), 10000);
    return () => clearInterval(timer);
  }, [token]);

  useEffect(() => {
    void load().catch(() => undefined);
    if (selected) void open(selected).catch(() => {
      setSelected(null);
      setEvents([]);
      setRules([]);
      setDecision(null);
    });
  }, [revision]);

  useEffect(() => {
    if (!selected) return;
    const timer = setInterval(() => void open(selected).catch(() => undefined), 10000);
    return () => clearInterval(timer);
  }, [selected, token]);

  async function open(id: string) {
    const deployment = await api<{ artifactId: string; status: string; previousArtifactId?: string }>(`/api/v1/deployments/${id}`, token);
    setSelected(id);
    setCanRollback(deployment.status === "Succeeded" && Boolean(deployment.previousArtifactId));
    const [timeline, evaluation, found] = await Promise.all([
      api<Array<{ sequence: number; type: string; message: string }>>(`/api/v1/deployments/${id}/events`, token),
      api<{ decision: string; ruleResults: Array<{ type: string; passed: boolean; message: string }> }>(`/api/v1/deployments/${id}/evaluation`, token).catch(() => null),
      api<Array<{ id: string; severity: string; title: string; cve?: string; packageName?: string; status: string }>>(`/api/v1/artifacts/${deployment.artifactId}/findings`, token).catch(() => []),
    ]);
    setEvents(timeline);
    setDecision(evaluation?.decision ?? null);
    setFindings(found);
    setRules((evaluation?.ruleResults ?? []).map((rule) => {
      const row = rule as { type?: string; Type?: string; passed?: boolean; Passed?: boolean; message?: string; Message?: string };
      return { type: row.type ?? row.Type ?? "rule", passed: row.passed ?? row.Passed ?? false, message: row.message ?? row.Message ?? "" };
    }));
  }

  return (
    <div className="split-layout">
      <section className="panel"><div className="panel-heading"><h2>Recent deployments</h2><span>{items.length} total</span></div><ul className="item-list">
        {items.map((item) => (
          <li key={item.id}>
            <button className={`list-button ${selected === item.id ? "is-selected" : ""}`} type="button" onClick={() => void open(item.id).catch((error: Error) => onError(error.message))}>
              <strong>{item.status}</strong><span>Approvals {item.approvalsReceived}/{item.approvalsRequired}</span>
              {item.failureReason && <span className="warning-text">{item.failureReason}</span>}
            </button>
          </li>
        ))}
      </ul>{items.length === 0 && <p className="empty-state">No deployments to show yet.</p>}</section>
      <section className="panel"><div className="panel-heading"><h2>Deployment details</h2></div><div className="detail-stack">
        {selected && canRollback && (
          <button className="secondary-button" type="button" onClick={() => void api(`/api/v1/deployments/${selected}/rollback`, token, { method: "POST" }).then(() => load()).catch((error: Error) => onError(error.message))}>Rollback</button>
        )}
        {decision && <p className="detail-row">Policy decision: <strong>{decision}</strong></p>}
        {rules.length > 0 && <h3 className="subheading">Policy checks</h3>}
        <ul className="item-list">
          {rules.map((rule) => <li key={`${rule.type}-${rule.message}`} className="detail-row">{rule.passed ? "Pass" : "Fail"} · {rule.type}: {rule.message}</li>)}
        </ul>
        {findings.length > 0 && <h3 className="subheading">Findings</h3>}
        <ul className="item-list">
          {findings.map((finding) => <li key={finding.id} className="detail-row">{finding.severity} · {finding.title}{finding.cve ? ` · ${finding.cve}` : ""}{finding.packageName ? ` · ${finding.packageName}` : ""} · {finding.status}</li>)}
        </ul>
        {events.length > 0 && <h3 className="subheading">Timeline</h3>}
        <ol className="item-list">
          {events.map((item) => <li key={item.sequence} className="detail-row">{item.type}: {item.message}</li>)}
        </ol>
        {!selected && <p className="empty-state">Select a deployment to inspect its timeline and policy checks.</p>}
      </div></section>
    </div>
  );
}

function Approvals({ token, onError, revision }: { token: string; onError: (value: string | null) => void; revision: number }) {
  const [items, setItems] = useState<Array<{ id: string; status: string }>>([]);
  useEffect(() => {
    api<Array<{ id: string; status: string }>>("/api/v1/deployments?status=AwaitingApproval", token).then(setItems).catch((error: Error) => onError(error.message));
  }, [token, onError, revision]);

  async function decide(id: string, decision: "Approved" | "Rejected") {
    await api(`/api/v1/deployments/${id}/approvals`, token, { method: "POST", body: JSON.stringify({ decision, comment: decision }) });
    setItems(await api("/api/v1/deployments?status=AwaitingApproval", token));
  }

  return (
    <ul className="panel item-list">
      {items.map((item) => (
        <li key={item.id} className="list-row">
          <span className="break-id">{item.id}</span>
          <span className="action-row">
            <button className="primary-button" type="button" onClick={() => void decide(item.id, "Approved").catch((error: Error) => onError(error.message))}>Approve</button>
            <button className="danger-button" type="button" onClick={() => void decide(item.id, "Rejected").catch((error: Error) => onError(error.message))}>Reject</button>
          </span>
        </li>
      ))}
      {items.length === 0 && <li className="empty-state">No requests awaiting approval.</li>}
    </ul>
  );
}

function Policies({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  type PolicyItem = { id: string; name: string; version: number; scope: string; isEnabled: boolean; appliesToTiers: string[]; rules: Array<{ type: string; effect: string; order: number; parameters: object }> };
  const [items, setItems] = useState<PolicyItem[]>([]);
  const [catalogue, setCatalogue] = useState<Array<{ type: string; effect: string; parameters: object }>>([]);
  const [choice, setChoice] = useState("");

  useEffect(() => {
    Promise.all([
      api<PolicyItem[]>("/api/v1/policies", token),
      api<Array<{ type: string; effect: string; parameters: object }>>("/api/v1/policies/rule-types", token),
    ]).then(([policies, types]) => {
      setItems(policies);
      setCatalogue(types);
      setChoice(types[0]?.type ?? "");
    }).catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function addRule(policy: PolicyItem) {
    const template = catalogue.find((item) => item.type === choice);
    if (!template) {
      return;
    }
    const rules = [...policy.rules, { type: template.type, effect: template.effect, order: policy.rules.length, parameters: template.parameters }];
    await api(`/api/v1/policies/${policy.id}`, token, { method: "PUT", body: JSON.stringify({ name: policy.name, scope: policy.scope, appliesToTiers: policy.appliesToTiers, rules }) });
    setItems(await api("/api/v1/policies", token));
  }

  return (
    <div className="detail-stack">
      <div className="panel"><label className="field-label policy-select">
        Rule to add
        <select value={choice} onChange={(event) => setChoice(event.target.value)}>
          {catalogue.map((item) => <option key={item.type} value={item.type}>{item.type}</option>)}
        </select>
      </label></div>
      <ul className="policy-list">
        {items.map((policy) => (
          <li key={policy.id} className="panel">
            <div className="panel-heading policy-heading">
              <h2>{policy.name} <span>v{policy.version}</span></h2>
              <button className="primary-button" type="button" onClick={() => void addRule(policy).catch((error: Error) => onError(error.message))}>Add rule</button>
            </div>
            <ul className="item-list">
              {policy.rules.map((rule) => <li className="detail-row" key={`${rule.type}-${rule.order}`}>{rule.order}. {rule.type} ({rule.effect})</li>)}
            </ul>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Admin({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [users, setUsers] = useState<Array<{ id: string; email: string; displayName: string; roles: string[] }>>([]);
  const [teams, setTeams] = useState<Array<{ slug: string; name: string }>>([]);
  const [jobs, setJobs] = useState<Array<{ id: string; type: string; status: string; lastError?: string }>>([]);
  const [queue, setQueue] = useState<{ pending: number; running: number; deadLettered: number } | null>(null);
  useEffect(() => {
    Promise.all([
      api<Array<{ id: string; email: string; displayName: string; roles: string[] }>>("/api/v1/admin/users", token),
      api<Array<{ slug: string; name: string }>>("/api/v1/teams", token),
      api<Array<{ id: string; type: string; status: string; lastError?: string }>>("/api/v1/admin/jobs?status=DeadLettered", token).catch(() => []),
      api<{ pending: number; running: number; deadLettered: number }>("/api/v1/admin/jobs/summary", token).catch(() => null),
    ]).then(([accounts, groups, dead, depth]) => {
      setUsers(accounts);
      setTeams(groups);
      setJobs(dead);
      setQueue(depth);
    }).catch((error: Error) => onError(error.message));
  }, [token, onError]);

  return (
    <div className="admin-grid">
      <section className="panel">
        <div className="panel-heading"><h2>Users</h2><span>{users.length} total</span></div>
        <ul className="item-list">
          {users.map((user) => <li key={user.id} className="detail-row">{user.displayName} · {user.email} · {user.roles.join(", ")}</li>)}
        </ul>
      </section>
      <section className="panel">
        <div className="panel-heading"><h2>Teams</h2><span>{teams.length} total</span></div>
        <ul className="item-list">
          {teams.map((team) => <li key={team.slug} className="detail-row">{team.name}</li>)}
        </ul>
      </section>
      <section className="panel jobs-panel">
        <div className="panel-heading"><h2>Jobs</h2></div>
        {queue && <p className="queue-summary">Pending {queue.pending} · running {queue.running} · dead-letter {queue.deadLettered}</p>}
        <ul className="item-list">
          {jobs.map((job) => (
            <li key={job.id} className="list-row">
              <span>{job.type} · {job.status}{job.lastError ? ` · ${job.lastError}` : ""}</span>
              <button className="secondary-button" type="button" onClick={() => void api(`/api/v1/admin/jobs/${job.id}/retry`, token, { method: "POST" }).then(() => api<typeof jobs>("/api/v1/admin/jobs?status=DeadLettered", token).then(setJobs)).catch((error: Error) => onError(error.message))}>Retry</button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}

function Audit({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<Array<{ id: string; timestamp: string; action: string; outcome: string; actorDisplay: string }>>([]);
  useEffect(() => {
    api<typeof items>("/api/v1/audit", token).then(setItems).catch((error: Error) => onError(error.message));
  }, [token, onError]);
  return (
    <ul className="panel item-list">
      {items.map((item) => <li key={item.id} className="audit-row"><time>{item.timestamp}</time><span>{item.actorDisplay}</span><strong>{item.action}</strong><span>{item.outcome}</span></li>)}
      {items.length === 0 && <li className="empty-state">No audit activity to show yet.</li>}
    </ul>
  );
}
