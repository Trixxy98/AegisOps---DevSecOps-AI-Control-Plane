import { useCallback, useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "./api";

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
    <div className="environment-variables" aria-label={`${environmentName} variables`} role="group">
      <p className="environment-title">
        Variables <span>{variables.length} configured</span>
      </p>
      {variables.length === 0 ? (
        <p className="empty-state">
          No variables. The container only receives DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT.
        </p>
      ) : (
        <ul className="variable-list" aria-label={`${environmentName} variable list`}>
          {variables.map((variable) => (
            <li key={variable.name} className="list-row">
              <span>
                <code>{variable.name}</code> = <code>{variable.isSecret ? "••••• (secret)" : variable.value}</code>
              </span>
              {canEdit && (
                <button
                  className="secondary-button"
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
          <div className="form-grid">
            <label className="field-label">
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
            <label className="field-label">
              Value
              <input
                value={value}
                onChange={(event) => setValue(event.target.value)}
                type={isSecret ? "password" : "text"}
                autoComplete="off"
                maxLength={8192}
              />
            </label>
          </div>
          <div className="action-row">
            <label className="checkbox-label">
              <input type="checkbox" checked={isSecret} onChange={(event) => setIsSecret(event.target.checked)} />
              Secret (write-only, encrypted)
            </label>
            <button className="primary-button" type="submit" disabled={saving || name.trim() === ""}>
              Save variable
            </button>
          </div>
        </form>
      )}
      {error && (
        <p role="alert" className="inline-error">
          {error}
        </p>
      )}
    </div>
  );
}
