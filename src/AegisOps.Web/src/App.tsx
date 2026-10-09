import { useEffect, useState } from "react";

type View = "projects" | "deployments" | "approvals" | "policies" | "audit";

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
  const [view, setView] = useState<View>("projects");
  const [error, setError] = useState<string | null>(null);

  if (!token) {
    return <Login onToken={setToken} />;
  }

  return (
    <main className="min-h-screen bg-slate-950 text-slate-100">
      <header className="flex items-center justify-between border-b border-slate-800 px-6 py-4">
        <div>
          <p className="text-xs tracking-wide text-slate-400">AegisOps</p>
          <h1 className="text-lg font-semibold">Control plane</h1>
        </div>
        <nav className="flex gap-2 text-sm">
          {(["projects", "deployments", "approvals", "policies", "audit"] as View[]).map((item) => (
            <button key={item} className={`rounded px-3 py-1 ${view === item ? "bg-slate-100 text-slate-950" : "bg-slate-900"}`} onClick={() => setView(item)} type="button">{item}</button>
          ))}
          <button className="rounded bg-slate-900 px-3 py-1" type="button" onClick={() => setToken(null)}>Logout</button>
        </nav>
      </header>
      <section className="mx-auto max-w-5xl px-6 py-6">
        {error && <p className="mb-4 rounded bg-red-950 px-3 py-2 text-sm text-red-200">{error}</p>}
        {view === "projects" && <Projects token={token} onError={setError} />}
        {view === "deployments" && <Deployments token={token} onError={setError} />}
        {view === "approvals" && <Approvals token={token} onError={setError} />}
        {view === "policies" && <Policies token={token} onError={setError} />}
        {view === "audit" && <Audit token={token} onError={setError} />}
      </section>
    </main>
  );
}

function Login({ onToken }: { onToken: (token: string) => void }) {
  const [email, setEmail] = useState("admin@aegisops.local");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  return (
    <main className="grid min-h-screen place-items-center bg-slate-950 text-slate-100">
      <form className="w-full max-w-sm space-y-3 rounded border border-slate-800 p-6" onSubmit={async (event) => {
        event.preventDefault();
        const response = await fetch("/api/v1/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ email, password }) });
        const body = await response.json();
        if (!response.ok) {
          setError(body.title ?? "Login failed");
          return;
        }
        onToken(body.accessToken);
      }}>
        <h1 className="text-xl font-semibold">Sign in</h1>
        {error && <p className="text-sm text-red-300">{error}</p>}
        <input className="w-full rounded bg-slate-900 px-3 py-2" value={email} onChange={(event) => setEmail(event.target.value)} />
        <input className="w-full rounded bg-slate-900 px-3 py-2" type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
        <button className="w-full rounded bg-slate-100 px-3 py-2 text-slate-950" type="submit">Continue</button>
      </form>
    </main>
  );
}

function Projects({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [projects, setProjects] = useState<Array<{ slug: string; name: string }>>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<{ environments: Array<{ id: string; name: string; tier: string }> } | null>(null);
  const [artifacts, setArtifacts] = useState<Array<{ id: string; version: string; branch: string }>>([]);

  useEffect(() => {
    api<Array<{ slug: string; name: string }>>("/api/v1/projects", token).then(setProjects).catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function open(slug: string) {
    setSelected(slug);
    const [project, items] = await Promise.all([
      api<{ environments: Array<{ id: string; name: string; tier: string }> }>(`/api/v1/projects/${slug}`, token),
      api<Array<{ id: string; version: string; branch: string }>>(`/api/v1/projects/${slug}/artifacts`, token),
    ]);
    setDetail(project);
    setArtifacts(items);
  }

  return (
    <div className="grid gap-6 md:grid-cols-2">
      <ul className="space-y-2">
        {projects.map((project) => (
          <li key={project.slug}><button className="w-full rounded bg-slate-900 px-3 py-2 text-left" type="button" onClick={() => void open(project.slug)}>{project.name}</button></li>
        ))}
      </ul>
      {selected && detail && (
        <div className="space-y-3">
          <h2 className="font-semibold">{selected}</h2>
          {artifacts.map((artifact) => (
            <article key={artifact.id} className="rounded border border-slate-800 p-3">
              <p>{artifact.version} · {artifact.branch}</p>
              <div className="mt-2 flex gap-2">
                {detail.environments.map((environment) => (
                  <button key={environment.id} className="rounded bg-slate-100 px-2 py-1 text-xs text-slate-950" type="button" onClick={() => void api("/api/v1/deployments", token, { method: "POST", body: JSON.stringify({ artifactId: artifact.id, environmentId: environment.id, reason: "UI request" }) }).then(() => onError(null)).catch((error: Error) => onError(error.message))}>
                    Deploy {environment.name}
                  </button>
                ))}
              </div>
            </article>
          ))}
        </div>
      )}
    </div>
  );
}

function Deployments({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<Array<{ id: string; status: string; approvalsReceived: number; approvalsRequired: number; failureReason?: string }>>([]);
  const [events, setEvents] = useState<Array<{ sequence: number; type: string; message: string }>>([]);

  async function load() {
    setItems(await api("/api/v1/deployments", token));
  }

  useEffect(() => {
    load().catch((error: Error) => onError(error.message));
    const timer = setInterval(() => void load().catch(() => undefined), 10000);
    return () => clearInterval(timer);
  }, [token]);

  return (
    <div className="grid gap-4 md:grid-cols-2">
      <ul className="space-y-2">
        {items.map((item) => (
          <li key={item.id}>
            <button className="w-full rounded bg-slate-900 px-3 py-2 text-left" type="button" onClick={() => void api<Array<{ sequence: number; type: string; message: string }>>(`/api/v1/deployments/${item.id}/events`, token).then(setEvents)}>
              <span className="font-medium">{item.status}</span>
              <span className="ml-2 text-slate-400">{item.approvalsReceived}/{item.approvalsRequired}</span>
              {item.failureReason && <span className="mt-1 block text-sm text-amber-200">{item.failureReason}</span>}
            </button>
          </li>
        ))}
      </ul>
      <ol className="space-y-2 text-sm">
        {events.map((item) => <li key={item.sequence} className="rounded bg-slate-900 px-3 py-2">{item.type}: {item.message}</li>)}
      </ol>
    </div>
  );
}

function Approvals({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<Array<{ id: string; status: string }>>([]);
  useEffect(() => {
    api<Array<{ id: string; status: string }>>("/api/v1/deployments?status=AwaitingApproval", token).then(setItems).catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function decide(id: string, decision: "Approved" | "Rejected") {
    await api(`/api/v1/deployments/${id}/approvals`, token, { method: "POST", body: JSON.stringify({ decision, comment: decision }) });
    setItems(await api("/api/v1/deployments?status=AwaitingApproval", token));
  }

  return (
    <ul className="space-y-2">
      {items.map((item) => (
        <li key={item.id} className="flex items-center justify-between rounded bg-slate-900 px-3 py-2">
          <span>{item.id}</span>
          <span className="space-x-2">
            <button className="rounded bg-slate-100 px-2 py-1 text-slate-950" type="button" onClick={() => void decide(item.id, "Approved").catch((error: Error) => onError(error.message))}>Approve</button>
            <button className="rounded bg-red-900 px-2 py-1" type="button" onClick={() => void decide(item.id, "Rejected").catch((error: Error) => onError(error.message))}>Reject</button>
          </span>
        </li>
      ))}
    </ul>
  );
}

function Policies({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<Array<{ id: string; name: string; version: number; isEnabled: boolean; appliesToTiers: string[]; rules: Array<{ type: string; effect: string; order: number; parameters: object }> }>>([]);

  useEffect(() => {
    api<typeof items>("/api/v1/policies", token).then(setItems).catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function addWindow(policy: (typeof items)[number]) {
    const rules = [...policy.rules, { type: "DeploymentWindow", effect: "Deny", order: policy.rules.length, parameters: { timezone: "Asia/Kuala_Lumpur", allowed: [{ days: ["Mon", "Tue", "Wed", "Thu"], from: "09:00", to: "18:00" }, { days: ["Fri"], from: "09:00", to: "15:00" }] } }];
    await api(`/api/v1/policies/${policy.id}`, token, { method: "PUT", body: JSON.stringify({ name: policy.name, scope: "Global", appliesToTiers: policy.appliesToTiers, rules }) });
    setItems(await api("/api/v1/policies", token));
  }

  return (
    <ul className="space-y-3">
      {items.map((policy) => (
        <li key={policy.id} className="rounded border border-slate-800 p-3">
          <div className="flex items-center justify-between">
            <h2 className="font-semibold">{policy.name} · v{policy.version}</h2>
            <button className="rounded bg-slate-100 px-2 py-1 text-xs text-slate-950" type="button" onClick={() => void addWindow(policy).catch((error: Error) => onError(error.message))}>Add Friday window</button>
          </div>
          <ul className="mt-2 text-sm text-slate-300">
            {policy.rules.map((rule) => <li key={`${rule.type}-${rule.order}`}>{rule.order}. {rule.type} ({rule.effect})</li>)}
          </ul>
        </li>
      ))}
    </ul>
  );
}

function Audit({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<Array<{ id: string; timestamp: string; action: string; outcome: string; actorDisplay: string }>>([]);
  useEffect(() => {
    api<typeof items>("/api/v1/audit", token).then(setItems).catch((error: Error) => onError(error.message));
  }, [token, onError]);
  return (
    <ul className="space-y-2 text-sm">
      {items.map((item) => <li key={item.id} className="rounded bg-slate-900 px-3 py-2">{item.timestamp} · {item.actorDisplay} · {item.action} · {item.outcome}</li>)}
    </ul>
  );
}
