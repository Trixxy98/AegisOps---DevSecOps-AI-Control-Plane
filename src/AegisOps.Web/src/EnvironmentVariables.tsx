import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "./api";
import { Alert } from "./ui";

/** GET /api/v1/projects/{slug}/environments/{envId}/variables (EnvironmentVariableEndpoints.cs). Secret values are always null. */
type EnvironmentVariable = { name: string; isSecret: boolean; value: string | null; updatedAt: string };

function problemMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return [error.problem.title ?? error.message, error.problem.detail].filter(Boolean).join(" ");
  }
  return error instanceof Error ? error.message : "Request failed.";
}

/**
 * Per-environment configuration injected into the container at deploy time. Secret values are write-only: the API
 * never returns them, so the list shows a placeholder and saving a secret replaces it.
 */
export function EnvironmentVariables({
  token,
  slug,
  environmentId,
  environmentName,
  canEdit,
}: {
  token: string;
  slug: string;
  environmentId: string;
  environmentName: string;
  canEdit: boolean;
}) {
  const basePath = `/api/v1/projects/${slug}/environments/${environmentId}/variables`;
  const [variables, setVariables] = useState<EnvironmentVariable[]>([]);
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  const [isSecret, setIsSecret] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const load = useCallback(
    () =>
      api<EnvironmentVariable[]>(basePath, token)
        .then((items) => setVariables(items))
        .catch((reason: unknown) => setError(problemMessage(reason))),
    [basePath, token],
  );

  useEffect(() => {
    void load();
  }, [load]);

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await api(`${basePath}/${encodeURIComponent(name.trim())}`, token, {
        method: "PUT",
        body: JSON.stringify({ value, isSecret }),
      });
      setName("");
      setValue("");
      setIsSecret(false);
      await load();
    } catch (reason) {
      setError(problemMessage(reason));
    } finally {
      setSaving(false);
    }
  }

  async function remove(variableName: string) {
    setError(null);
    try {
      await api(`${basePath}/${encodeURIComponent(variableName)}`, token, { method: "DELETE" });
      await load();
    } catch (reason) {
      setError(problemMessage(reason));
    }
  }

  return (
    <div className="subsection" aria-label={`${environmentName} variables`} role="group">
      <div className="subsection-head">
        <h3 className="section-title">Variables</h3>
        <span className="card-meta">{variables.length}</span>
      </div>
      {variables.length === 0 ? (
        <p className="field-hint">
          No variables. The container only receives DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT.
        </p>
      ) : (
        <ul className="list inset-list" aria-label={`${environmentName} variable list`}>
          {variables.map((variable) => (
            <li key={variable.name}>
              <span className="variable">
                <code className="variable-name" title={variable.name}>
                  {variable.name}
                </code>
                <span className="subtle">=</span>
                {variable.isSecret ? (
                  <code className="mono">••••• (secret)</code>
                ) : (
                  <code className="mono" title={variable.value ?? undefined}>
                    {variable.value}
                  </code>
                )}
              </span>
              {canEdit && (
                <button
                  className="btn btn-ghost btn-sm"
                  type="button"
                  aria-label={`Delete ${variable.name}`}
                  onClick={() => void remove(variable.name)}
                >
                  Delete
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      {canEdit && (
        <form className="variable-form" onSubmit={(event) => void save(event)}>
          <label className="field">
            Variable name
            <input
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="ConnectionStrings__Default"
              required
              maxLength={128}
              pattern="[A-Za-z_][A-Za-z0-9_]*"
            />
          </label>
          <label className="field">
            Value
            <input
              value={value}
              onChange={(event) => setValue(event.target.value)}
              type={isSecret ? "password" : "text"}
              autoComplete="off"
              maxLength={8192}
            />
          </label>
          <label className="checkbox variable-secret">
            <input type="checkbox" checked={isSecret} onChange={(event) => setIsSecret(event.target.checked)} />
            Secret (write-only, encrypted)
          </label>
          <button className="btn btn-secondary" type="submit" disabled={saving || name.trim() === ""}>
            Save variable
          </button>
        </form>
      )}
      {error && <Alert>{error}</Alert>}
    </div>
  );
}
