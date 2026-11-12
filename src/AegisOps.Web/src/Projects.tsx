import { useEffect, useState } from "react";
import { api } from "./api";
import { CreateProject } from "./CreateProject";
import { EnvironmentVariables } from "./EnvironmentVariables";
import { formatDateTime } from "./format";
import { IconFolder } from "./icons";
import { Alert, CardHeader, EmptyState, Spinner, StatusBadge } from "./ui";

export type DeployTarget = { id: string; name: string; tier: string };

type Environment = {
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
};

/** API keys created from the Projects page expire after 90 days. */
function apiKeyExpiry(): string {
  return new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString();
}

export function Projects({
  token,
  permissions,
  onError,
  pendingDeployments,
  onDeploy,
}: {
  token: string;
  permissions: string[];
  onError: (value: string | null) => void;
  pendingDeployments: string[];
  onDeploy: (artifact: { id: string; version: string }, environment: DeployTarget) => Promise<void>;
}) {
  const [projects, setProjects] = useState<Array<{ slug: string; name: string }>>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<{ environments: Environment[] } | null>(null);
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

  async function projectCreated(project: { slug: string; name: string }) {
    setProjects((current) =>
      [...current.filter((item) => item.slug !== project.slug), { slug: project.slug, name: project.name }].sort(
        (left, right) => left.name.localeCompare(right.name),
      ),
    );
    await open(project.slug);
  }

  async function open(slug: string) {
    setSelected(slug);
    const [project, items, apiKeys] = await Promise.all([
      api<{ environments: Environment[] }>(`/api/v1/projects/${slug}`, token),
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
    const expiresAt = apiKeyExpiry();
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

  const selectedName = projects.find((project) => project.slug === selected)?.name;

  return (
    <div className="split">
      <div className="stack">
        <section className="card">
          <CardHeader title="Projects" meta={projects.length} />
          {projects.length === 0 ? (
            <p className="empty-row">No projects yet.</p>
          ) : (
            <ul className="list">
              {projects.map((project) => (
                <li key={project.slug}>
                  <button
                    className={`list-button ${selected === project.slug ? "is-selected" : ""}`}
                    type="button"
                    aria-current={selected === project.slug ? "true" : undefined}
                    onClick={() => void open(project.slug)}
                  >
                    <span className="list-primary">
                      <span className="text">{project.name}</span>
                    </span>
                    <span className="list-secondary">
                      <code className="mono">{project.slug}</code>
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>
        {permissions.includes("projects:write") && (
          <section className="card">
            <CardHeader title="New project" />
            <div className="card-body">
              <CreateProject
                token={token}
                onCreated={(project) => void projectCreated(project).catch((error: Error) => onError(error.message))}
              />
            </div>
          </section>
        )}
      </div>

      {selected && detail ? (
        <div className="stack-lg">
          <h2 className="project-title">
            {selectedName ?? selected} <code className="mono">{selected}</code>
          </h2>

          <section className="card">
            <CardHeader title="Artifacts" meta={artifacts.length} />
            {artifacts.length === 0 ? (
              <p className="empty-row">No artifacts yet. CI registers them with a project API key.</p>
            ) : (
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th scope="col">Version</th>
                      <th scope="col">Branch</th>
                      <th scope="col" className="actions">
                        Deploy
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {artifacts.map((artifact) => (
                      <tr key={artifact.id}>
                        <td>
                          <code className="mono variable-name" title={artifact.id}>
                            {artifact.version}
                          </code>
                        </td>
                        <td className="truncate" title={artifact.branch}>
                          {artifact.branch}
                        </td>
                        <td className="actions">
                          <span className="row">
                            {detail.environments.map((environment) => {
                              const pending = pendingDeployments.includes(`${artifact.id}:${environment.id}`);
                              return (
                                <button
                                  key={environment.id}
                                  className="btn btn-secondary btn-sm"
                                  type="button"
                                  disabled={pending}
                                  onClick={() => void onDeploy(artifact, environment)}
                                >
                                  {pending ? (
                                    <>
                                      <Spinner /> Requesting {environment.name}…
                                    </>
                                  ) : (
                                    `Deploy ${environment.name}`
                                  )}
                                </button>
                              );
                            })}
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          <section className="card">
            <CardHeader title="Environments" meta={detail.environments.length} />
            {detail.environments.map((environment) => (
              <div key={environment.id} className="environment">
                <form
                  className="form"
                  aria-label={`${environment.name} target`}
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
                  <div className="environment-head">
                    <p className="environment-name">
                      {environment.name}
                      <span className="tag">{environment.tier}</span>
                      <span className="tag">{environment.targetType}</span>
                    </p>
                    <div className="form-actions">
                      <button
                        className="btn btn-ghost btn-sm"
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
                      <button className="btn btn-secondary btn-sm" type="submit">
                        Save target
                      </button>
                    </div>
                  </div>
                  <div className="form-grid">
                    <label className="field">
                      Target type
                      <select defaultValue={environment.targetType} name="type">
                        <option value="noop">noop</option>
                        <option value="docker">docker</option>
                      </select>
                    </label>
                    <label className="field">
                      Container name
                      <input
                        defaultValue={environment.containerName ?? ""}
                        name="containerName"
                        placeholder="Container name"
                      />
                    </label>
                    <label className="field">
                      Network
                      <input defaultValue={environment.network ?? ""} name="network" placeholder="Network" />
                    </label>
                    <label className="field">
                      Host port
                      <input defaultValue={environment.hostPort || 8102} name="hostPort" inputMode="numeric" />
                    </label>
                    <label className="field">
                      Container port
                      <input
                        defaultValue={environment.containerPort || 8080}
                        name="containerPort"
                        inputMode="numeric"
                      />
                    </label>
                    <label className="field">
                      Health path
                      <input defaultValue={environment.healthPath ?? "/health"} name="healthPath" />
                    </label>
                    <label className="field">
                      Health timeout (seconds)
                      <input
                        defaultValue={environment.healthTimeoutSeconds || 60}
                        name="healthTimeoutSeconds"
                        inputMode="numeric"
                      />
                    </label>
                  </div>
                </form>
                <EnvironmentVariables
                  token={token}
                  slug={selected}
                  environmentId={environment.id}
                  environmentName={environment.name}
                  canEdit={permissions.includes("environments:write")}
                />
              </div>
            ))}
            {runtime && (
              <div className="card-body">
                <p className="section-title">Runtime</p>
                <pre className="code-block">{runtime}</pre>
              </div>
            )}
          </section>

          <section className="card">
            <CardHeader title="API keys" meta={keys.length} />
            <div className="card-body">
              <div className="toolbar toolbar-flush">
                <label className="field">
                  Key name
                  <input value={keyName} onChange={(event) => setKeyName(event.target.value)} />
                </label>
                <button
                  className="btn btn-primary"
                  type="button"
                  onClick={() => void createKey().catch((error: Error) => onError(error.message))}
                >
                  Create
                </button>
              </div>
              {plaintext && (
                <Alert tone="info" title="Copy this key now. It is not shown again.">
                  <code className="secret-value">{plaintext}</code>
                </Alert>
              )}
            </div>
            {keys.length > 0 && (
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th scope="col">Name</th>
                      <th scope="col">Prefix</th>
                      <th scope="col">Last used</th>
                      <th scope="col">Status</th>
                      <th scope="col" className="actions">
                        <span className="sr-only">Actions</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {keys.map((key) => (
                      <tr key={key.id}>
                        <td className="truncate" title={key.name}>
                          {key.name}
                        </td>
                        <td>
                          <code className="mono">{key.keyPrefix}</code>
                        </td>
                        <td className="nowrap tabular">
                          {key.lastUsedAt ? formatDateTime(key.lastUsedAt) : <span className="subtle">Never</span>}
                        </td>
                        <td>
                          {key.revokedAt ? (
                            <StatusBadge status="Revoked" tone="neutral" />
                          ) : (
                            <StatusBadge status="Active" />
                          )}
                        </td>
                        <td className="actions">
                          {!key.revokedAt && (
                            <button
                              className="btn btn-danger btn-sm"
                              type="button"
                              onClick={() => void revokeKey(key.id).catch((error: Error) => onError(error.message))}
                            >
                              Revoke
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </div>
      ) : (
        <section className="card">
          <EmptyState icon={<IconFolder />}>
            Select a project to see its artifacts, environments and API keys.
          </EmptyState>
        </section>
      )}
    </div>
  );
}
