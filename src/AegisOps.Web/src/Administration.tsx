import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { api } from "./api";
import { formatDateTime, humanize } from "./format";
import { IconCheck } from "./icons";
import { Alert, CardHeader, EmptyState, StatusBadge } from "./ui";
import { UserManagement } from "./UserManagement";

type Delivery = {
  eventId: string;
  eventType: string;
  occurredAt: string;
  status: string;
  attempts: number;
  maxAttempts: number;
  completedAt: string | null;
  lastError: string | null;
};

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

const tabs = [
  { id: "users", label: "Users" },
  { id: "teams", label: "Teams" },
  { id: "jobs", label: "Jobs" },
] as const;
type Tab = (typeof tabs)[number]["id"];

export function Administration({
  token,
  onError,
  permissions,
  currentUserId,
}: {
  token: string;
  onError: (value: string | null) => void;
  permissions: string[];
  currentUserId: string | null;
}) {
  const [tab, setTab] = useState<Tab>("users");
  const tabRefs = useRef<Partial<Record<Tab, HTMLButtonElement | null>>>({});
  const [teams, setTeams] = useState<Array<{ slug: string; name: string; myRole?: string | null }>>([]);
  const [webhooks, setWebhooks] = useState<Record<string, { url: string; configured: boolean; eventTypes: string[] }>>(
    {},
  );
  const [webhookUrls, setWebhookUrls] = useState<Record<string, string>>({});
  const [webhookSecrets, setWebhookSecrets] = useState<Record<string, string>>({});
  const [savingWebhook, setSavingWebhook] = useState<string | null>(null);
  const [webhookMessages, setWebhookMessages] = useState<Record<string, string>>({});
  const [webhookEvents, setWebhookEvents] = useState<Record<string, string[]>>({});
  const [webhookDeliveries, setWebhookDeliveries] = useState<Record<string, Delivery[]>>({});
  const [jobs, setJobs] = useState<Array<{ id: string; type: string; status: string; lastError?: string }>>([]);
  const [queue, setQueue] = useState<{ pending: number; running: number; deadLettered: number } | null>(null);
  useEffect(() => {
    Promise.all([
      api<Array<{ slug: string; name: string; myRole?: string | null }>>("/api/v1/teams", token),
      api<Array<{ id: string; type: string; status: string; lastError?: string }>>(
        "/api/v1/admin/jobs?status=DeadLettered",
        token,
      ).catch(() => []),
      api<{ pending: number; running: number; deadLettered: number }>("/api/v1/admin/jobs/summary", token).catch(
        () => null,
      ),
    ])
      .then(([groups, dead, depth]) => {
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
              const deliveries = await api<Delivery[]>(
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

  function onTabKey(event: KeyboardEvent<HTMLButtonElement>) {
    const index = tabs.findIndex((item) => item.id === tab);
    const step = event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0;
    if (!step) return;
    event.preventDefault();
    const next = tabs[(index + step + tabs.length) % tabs.length].id;
    setTab(next);
    tabRefs.current[next]?.focus();
  }

  return (
    <div className="stack-lg">
      <div className="tabs" role="tablist" aria-label="Administration sections">
        {tabs.map((item) => (
          <button
            key={item.id}
            ref={(element) => {
              tabRefs.current[item.id] = element;
            }}
            id={`admin-tab-${item.id}`}
            className="tab"
            type="button"
            role="tab"
            aria-selected={tab === item.id}
            aria-controls={`admin-panel-${item.id}`}
            tabIndex={tab === item.id ? 0 : -1}
            onClick={() => setTab(item.id)}
            onKeyDown={onTabKey}
          >
            {item.label}
            {item.id === "teams" && teams.length > 0 && <span className="tab-count">{teams.length}</span>}
            {item.id === "jobs" && !!queue?.deadLettered && <span className="tab-count">{queue.deadLettered}</span>}
          </button>
        ))}
      </div>

      <div id="admin-panel-users" role="tabpanel" aria-labelledby="admin-tab-users" hidden={tab !== "users"}>
        <UserManagement token={token} permissions={permissions} currentUserId={currentUserId} teams={teams} />
      </div>

      <div
        id="admin-panel-teams"
        role="tabpanel"
        aria-labelledby="admin-tab-teams"
        hidden={tab !== "teams"}
        className="stack-lg"
      >
        {teams.length === 0 && (
          <section className="card">
            <EmptyState>No teams yet.</EmptyState>
          </section>
        )}
        {teams.map((team) => {
          const deliveries = webhookDeliveries[team.slug] ?? [];
          const count = (predicate: (item: Delivery) => boolean) => deliveries.filter(predicate).length;
          return (
            <section key={team.slug} className="card" aria-labelledby={`team-${team.slug}`}>
              <CardHeader
                id={`team-${team.slug}`}
                title={
                  <>
                    {team.name} <code className="mono">{team.slug}</code>
                  </>
                }
                meta={team.myRole ? humanize(team.myRole) : undefined}
              />
              {team.myRole === "Owner" || !team.myRole ? (
                <div className="card-body">
                  <div className="row-between">
                    <h3 className="section-title">Outbound webhook</h3>
                    {webhooks[team.slug]?.configured ? (
                      <StatusBadge status="Configured" />
                    ) : (
                      <StatusBadge status="NotConfigured" tone="neutral" />
                    )}
                  </div>
                  <p className="field-hint">
                    Sends signed deployment events to a public HTTPS endpoint on port 443. Saving a new secret replaces
                    the existing one.
                  </p>
                  <div className="form-grid">
                    <div className="field">
                      <label htmlFor={`webhook-url-${team.slug}`}>HTTPS endpoint</label>
                      <input
                        id={`webhook-url-${team.slug}`}
                        type="url"
                        placeholder="https://hooks.example.com/aegisops"
                        value={webhookUrls[team.slug] ?? ""}
                        onChange={(event) =>
                          setWebhookUrls((current) => ({ ...current, [team.slug]: event.target.value }))
                        }
                      />
                    </div>
                    <div className="field">
                      <label htmlFor={`webhook-secret-${team.slug}`}>Signing secret (32–1024 bytes)</label>
                      <input
                        id={`webhook-secret-${team.slug}`}
                        type="password"
                        autoComplete="new-password"
                        value={webhookSecrets[team.slug] ?? ""}
                        onChange={(event) =>
                          setWebhookSecrets((current) => ({ ...current, [team.slug]: event.target.value }))
                        }
                      />
                    </div>
                  </div>
                  <fieldset className="checkbox-group">
                    <legend>Subscribed events</legend>
                    <div className="checkbox-grid-wide">
                      {eventTypes.map((eventType) => (
                        <label key={eventType} className="checkbox">
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
                    </div>
                  </fieldset>
                  {webhookMessages[team.slug] && (
                    <Alert tone="info" role="status">
                      {webhookMessages[team.slug]}
                    </Alert>
                  )}
                  <div className="form-actions">
                    <button
                      className="btn btn-primary"
                      type="button"
                      disabled={savingWebhook === team.slug}
                      onClick={() => void saveWebhook(team.slug)}
                    >
                      {savingWebhook === team.slug ? "Saving…" : "Save webhook"}
                    </button>
                  </div>

                  <div className="subsection">
                    <div className="row-between">
                      <h3 className="section-title">Recent deliveries</h3>
                      <span className="card-meta">{deliveries.length} events</span>
                    </div>
                    {deliveries.length > 0 && (
                      <div className="metric-row" aria-label="Recent delivery metrics">
                        <span className="metric">
                          <span>Delivered</span>
                          <strong>{count((item) => item.status === "Succeeded")}</strong>
                        </span>
                        <span className="metric">
                          <span>Dead-lettered</span>
                          <strong>{count((item) => item.status === "DeadLettered")}</strong>
                        </span>
                        <span className="metric">
                          <span>Queued</span>
                          <strong>{count((item) => item.status === "Pending" || item.status === "Running")}</strong>
                        </span>
                        <span className="metric">
                          <span>Not queued</span>
                          <strong>{count((item) => item.status === "NotQueued")}</strong>
                        </span>
                        <span className="metric">
                          <span>Attempts</span>
                          <strong>{deliveries.reduce((sum, item) => sum + item.attempts, 0)}</strong>
                        </span>
                      </div>
                    )}
                    {deliveries.length === 0 ? (
                      <p className="subtle">No recent deployment events.</p>
                    ) : (
                      <div className="table-wrap inset-list">
                        <table className="table">
                          <thead>
                            <tr>
                              <th scope="col">Event</th>
                              <th scope="col">Occurred</th>
                              <th scope="col">Status</th>
                              <th scope="col" className="num">
                                Attempts
                              </th>
                              <th scope="col">Last error</th>
                            </tr>
                          </thead>
                          <tbody>
                            {deliveries.slice(0, 10).map((delivery) => (
                              <tr key={delivery.eventId}>
                                <td>{delivery.eventType}</td>
                                <td className="nowrap tabular">{formatDateTime(delivery.occurredAt)}</td>
                                <td>
                                  <StatusBadge status={delivery.status} />
                                </td>
                                <td className="num">
                                  {delivery.maxAttempts > 0 ? `${delivery.attempts}/${delivery.maxAttempts}` : "—"}
                                </td>
                                <td className="truncate" title={delivery.lastError ?? undefined}>
                                  {delivery.lastError ?? <span className="subtle">—</span>}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    )}
                  </div>
                </div>
              ) : (
                <p className="empty-row">Webhook settings are visible to team owners and admins only.</p>
              )}
            </section>
          );
        })}
      </div>

      <div
        id="admin-panel-jobs"
        role="tabpanel"
        aria-labelledby="admin-tab-jobs"
        hidden={tab !== "jobs"}
        className="stack-lg"
      >
        {queue && (
          <div className="stat-grid">
            <article className="stat-card">
              <p>
                <span className="stat-dot tone-warning" aria-hidden="true" />
                Pending
              </p>
              <strong>{queue.pending}</strong>
            </article>
            <article className="stat-card">
              <p>
                <span className="stat-dot tone-info" aria-hidden="true" />
                Running
              </p>
              <strong>{queue.running}</strong>
            </article>
            <article className="stat-card">
              <p>
                <span className="stat-dot tone-danger" aria-hidden="true" />
                Dead-lettered
              </p>
              <strong>{queue.deadLettered}</strong>
            </article>
          </div>
        )}
        <section className="card">
          <CardHeader title="Dead-lettered jobs" meta={jobs.length} />
          {jobs.length === 0 ? (
            <EmptyState icon={<IconCheck />}>No dead-lettered jobs.</EmptyState>
          ) : (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th scope="col">Type</th>
                    <th scope="col">Status</th>
                    <th scope="col">Last error</th>
                    <th scope="col" className="actions">
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {jobs.map((job) => (
                    <tr key={job.id}>
                      <td title={job.id}>{humanize(job.type)}</td>
                      <td>
                        <StatusBadge status={job.status} />
                      </td>
                      <td className="truncate" title={job.lastError}>
                        {job.lastError ?? <span className="subtle">—</span>}
                      </td>
                      <td className="actions">
                        <button
                          className="btn btn-secondary btn-sm"
                          type="button"
                          onClick={() =>
                            void api(`/api/v1/admin/jobs/${job.id}/retry`, token, { method: "POST" })
                              .then(() =>
                                api<typeof jobs>("/api/v1/admin/jobs?status=DeadLettered", token).then(setJobs),
                              )
                              .catch((error: Error) => onError(error.message))
                          }
                        >
                          Retry
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </div>
    </div>
  );
}
