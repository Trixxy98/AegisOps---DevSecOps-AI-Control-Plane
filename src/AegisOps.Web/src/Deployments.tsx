import { useEffect, useEffectEvent, useState } from "react";
import { api } from "./api";
import { pollingFallbackMs } from "./deploymentUpdates";
import { formatDateTime, humanize, shortId } from "./format";
import { IconCheck, IconRocket, IconSparkles, IconX } from "./icons";
import { Alert, CardHeader, EmptyState, Loading, SeverityBadge, Spinner, StatusBadge } from "./ui";

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

function AiLabel() {
  return (
    <small className="ai-label">
      <IconSparkles size={12} />
      AI-generated — verify before acting.
    </small>
  );
}

export function Deployments({
  token,
  onError,
  revision,
  polling,
  onNavigate,
}: {
  token: string;
  onError: (value: string | null) => void;
  revision: number;
  polling: boolean;
  onNavigate: () => void;
}) {
  const [items, setItems] = useState<DeploymentListItem[]>([]);
  const [loaded, setLoaded] = useState(false);
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
    return api<DeploymentListItem[]>("/api/v1/deployments", token).then((list) => {
      setItems(list);
      setLoaded(true);
    });
  }

  // Effect events read the latest token/selection without re-subscribing the polling effects.
  const pollItems = useEffectEvent((reportErrors: boolean) => {
    load().catch((error: Error) => {
      setLoaded(true);
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
  const awaitingApproval = selected && selectedDeployment?.status === "AwaitingApproval";

  if (!loaded && items.length === 0) {
    return (
      <section className="card">
        <Loading>Loading deployments…</Loading>
      </section>
    );
  }

  if (items.length === 0) {
    return (
      <section className="card">
        <EmptyState
          icon={<IconRocket />}
          action={
            <button className="btn btn-secondary btn-sm" type="button" onClick={onNavigate}>
              Open projects
            </button>
          }
        >
          No deployments yet. Request one from a project&apos;s artifacts.
        </EmptyState>
      </section>
    );
  }

  return (
    <div className="split split-wide">
      <section className="card">
        <CardHeader title="Recent" meta={items.length} />
        <ul className="list">
          {recentItems.map(({ item, timestamp }, index) => (
            <li key={item.id}>
              <button
                className={`list-button ${selected === item.id ? "is-selected" : ""}`}
                type="button"
                onClick={() => void open(item.id).catch((error: Error) => onError(error.message))}
              >
                <span className="list-primary">
                  <StatusBadge status={item.status} />
                  <code className="mono" title={item.id}>
                    {shortId(item.id)}
                  </code>
                  {index === 0 && timestamp !== null && <span className="latest-marker">Latest</span>}
                </span>
                <span className="list-secondary tabular">
                  {timestamp !== null ? (
                    <time dateTime={item.requestedAt ?? undefined}>{formatDateTime(item.requestedAt)}</time>
                  ) : (
                    "Time unavailable"
                  )}
                  {item.approvalsRequired > 0 && (
                    <span className="dot-sep">
                      Approvals {item.approvalsReceived}/{item.approvalsRequired}
                    </span>
                  )}
                </span>
                {item.failureReason && <span className="list-secondary danger-text">{item.failureReason}</span>}
              </button>
            </li>
          ))}
        </ul>
      </section>

      <section className="card" aria-labelledby="deployment-detail-heading">
        {!selected ? (
          <EmptyState icon={<IconRocket />}>Select a deployment to see its policy checks and timeline.</EmptyState>
        ) : (
          <>
            <CardHeader
              id="deployment-detail-heading"
              title={
                <>
                  Deployment{" "}
                  <code className="mono" title={selected}>
                    {shortId(selected)}
                  </code>
                </>
              }
              meta={selectedDeployment ? <StatusBadge status={selectedDeployment.status} /> : undefined}
              actions={
                <>
                  {canRollback && (
                    <button
                      className="btn btn-secondary btn-sm"
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
                  {awaitingApproval && (
                    <button
                      className="btn btn-secondary btn-sm"
                      type="button"
                      disabled={summaryPendingId === selected}
                      aria-busy={summaryPendingId === selected}
                      onClick={() => void summarizeDeployment(selected)}
                    >
                      {summaryPendingId === selected ? (
                        <>
                          <Spinner />
                          Generating summary…
                        </>
                      ) : (
                        <>
                          <IconSparkles size={14} />
                          Generate AI summary
                        </>
                      )}
                    </button>
                  )}
                </>
              }
            />
            <div className="card-body">
              {awaitingApproval && summaryError?.deploymentId === selected && <Alert>{summaryError.message}</Alert>}
              {awaitingApproval && aiSummary?.deploymentId === selected && (
                <div className="ai-result" aria-live="polite">
                  <strong>Deployment summary</strong>
                  <p>{aiSummary.text}</p>
                  <AiLabel />
                </div>
              )}

              {(decision || rules.length > 0) && (
                <div className="stack">
                  <div className="row-between">
                    <h3 className="section-title">Policy checks</h3>
                    {decision && (
                      <span className="row subtle">
                        Decision <StatusBadge status={decision} />
                      </span>
                    )}
                  </div>
                  {rules.length > 0 && (
                    <ul className="list check-list">
                      {rules.map((rule) => (
                        <li key={`${rule.type}-${rule.message}`}>
                          <span className={`check-icon ${rule.passed ? "pass" : "fail"}`}>
                            {rule.passed ? <IconCheck size={12} /> : <IconX size={12} />}
                          </span>
                          <span className="check-text">
                            <strong>
                              <span className="sr-only">{rule.passed ? "Pass" : "Fail"}: </span>
                              {humanize(rule.type)}
                            </strong>
                            {rule.message && <span>{rule.message}</span>}
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              )}

              {findings.length > 0 && (
                <div className="stack">
                  <h3 className="section-title">Findings in this artifact</h3>
                  <ul className="list">
                    {findings.map((finding) => (
                      <li key={finding.id} className="finding-row">
                        <div className="finding-row-head">
                          <span className="finding-row-title">
                            <SeverityBadge severity={finding.severity} />
                            <span>{finding.title}</span>
                          </span>
                          <button
                            className="btn btn-ghost btn-sm"
                            type="button"
                            disabled={explanationPendingId === finding.id}
                            aria-busy={explanationPendingId === finding.id}
                            onClick={() => void explainFinding(finding.id)}
                          >
                            {explanationPendingId === finding.id ? (
                              <>
                                <Spinner />
                                Explaining…
                              </>
                            ) : (
                              <>
                                <IconSparkles size={14} />
                                Explain with AI
                              </>
                            )}
                          </button>
                        </div>
                        <span className="list-secondary">
                          {finding.cve && <code className="mono">{finding.cve}</code>}
                          {finding.packageName && (
                            <span className={finding.cve ? "dot-sep" : ""}>{finding.packageName}</span>
                          )}
                          <span className={finding.cve || finding.packageName ? "dot-sep" : ""}>{finding.status}</span>
                        </span>
                        {explanationError?.findingId === finding.id && <Alert>{explanationError.message}</Alert>}
                        {findingExplanation?.findingId === finding.id && (
                          <div className="ai-result" aria-live="polite">
                            <strong>Finding explanation</strong>
                            <p>{findingExplanation.text}</p>
                            <AiLabel />
                          </div>
                        )}
                      </li>
                    ))}
                  </ul>
                </div>
              )}

              <div className="stack">
                <h3 className="section-title">Timeline</h3>
                {events.length === 0 ? (
                  <p className="subtle">No events yet.</p>
                ) : (
                  <ol className="timeline">
                    {events.map((item) => (
                      <li key={item.sequence}>
                        <span className="timeline-type">{humanize(item.type)}</span>
                        <span className="sr-only">: </span>
                        <span className="timeline-message">{item.message}</span>
                      </li>
                    ))}
                  </ol>
                )}
              </div>
            </div>
          </>
        )}
      </section>
    </div>
  );
}
