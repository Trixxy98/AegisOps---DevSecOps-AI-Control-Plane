import { useEffect, useState } from "react";
import { api } from "./api";
import { formatDateTime, humanize } from "./format";
import { IconChevronLeft, IconChevronRight, IconSparkles } from "./icons";
import { Alert, CardHeader, EmptyState, Loading, StatusBadge } from "./ui";

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

const numberFormat = new Intl.NumberFormat();

export function AiGovernance({ token }: { token: string }) {
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
    <div className="stack-lg">
      <section className="card" aria-labelledby="ai-requests-heading">
        <CardHeader id="ai-requests-heading" title="Request history" meta={`${requestTotal} total`} />
        <p className="card-note">
          Prompts and responses are not shown; only detections, redactions and outcome metadata.
        </p>
        {requestError && (
          <div className="card-body">
            <Alert>{requestError}</Alert>
          </div>
        )}
        {requestLoading ? (
          <Loading>Loading AI requests…</Loading>
        ) : !requests.length ? (
          <EmptyState icon={<IconSparkles />}>
            {requestTotal ? "No requests on this page." : "No AI requests recorded yet."}
          </EmptyState>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th scope="col">Time</th>
                  <th scope="col">Purpose</th>
                  <th scope="col">Outcome</th>
                  <th scope="col" className="num">
                    Prompt / response
                  </th>
                  <th scope="col" className="num">
                    Tokens
                  </th>
                  <th scope="col" className="num">
                    Latency
                  </th>
                  <th scope="col">Sensitive data</th>
                  <th scope="col">Block reason</th>
                </tr>
              </thead>
              <tbody>
                {requests.map((request) => {
                  const detected = jsonStringArray(request.detectedSensitiveTypesJson);
                  return (
                    <tr key={request.id}>
                      <td className="nowrap tabular">
                        <time dateTime={request.createdAt}>{formatDateTime(request.createdAt)}</time>
                      </td>
                      <td>{humanize(request.purpose)}</td>
                      <td>
                        <StatusBadge status={request.status} />
                      </td>
                      <td className="num" title="Characters">
                        {numberFormat.format(request.promptChars)} / {numberFormat.format(request.responseChars)}
                      </td>
                      <td className="num">
                        {request.promptTokens ?? "—"} / {request.completionTokens ?? "—"}
                      </td>
                      <td className="num">{numberFormat.format(request.latencyMs)} ms</td>
                      <td>
                        {detected.length ? (
                          <span className="row">
                            {detected.map((type) => (
                              <code key={type} className="tag">
                                {type}
                              </code>
                            ))}
                            {request.redactionsApplied > 0 && (
                              <span className="subtle">{request.redactionsApplied} redacted</span>
                            )}
                          </span>
                        ) : (
                          <span className="subtle">None</span>
                        )}
                      </td>
                      <td>{request.blockReason ?? <span className="subtle">—</span>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <div className="card-footer">
          <span className="tabular">
            Page {page} of {pageCount}
          </span>
          <span className="row">
            <button
              className="btn btn-secondary btn-sm"
              type="button"
              disabled={page <= 1 || requestLoading}
              onClick={() => goToPage(Math.max(1, page - 1))}
            >
              <IconChevronLeft size={14} />
              Previous
            </button>
            <button
              className="btn btn-secondary btn-sm"
              type="button"
              disabled={page >= pageCount || requestLoading}
              onClick={() => goToPage(Math.min(pageCount, page + 1))}
            >
              Next
              <IconChevronRight size={14} />
            </button>
          </span>
        </div>
      </section>

      <section className="card" aria-labelledby="ai-policy-heading">
        <CardHeader
          id="ai-policy-heading"
          title="Policy controls"
          meta={
            selectedPolicy
              ? `Version ${selectedPolicy.version} · ${selectedPolicy.allowedModelIds.length} allowed model(s)`
              : undefined
          }
        />
        <div className="card-body">
          <p className="field-hint">
            Admins edit the Global policy; Security edits team policies. Allowed models are kept when saving.
          </p>
          {policyError && <Alert>{policyError}</Alert>}
          {policyMessage && (
            <Alert tone="success" role="status">
              {policyMessage}
            </Alert>
          )}
          {policyLoading ? (
            <Loading>Loading AI policies…</Loading>
          ) : policies.length === 0 ? (
            <p className="subtle">No AI policies are configured.</p>
          ) : (
            <>
              <label className="field policy-picker" htmlFor="ai-policy-select">
                AI policy
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
              </label>
              {selectedPolicy && (
                <div className="form">
                  <div className="form-grid">
                    <label className="field">
                      Requests per minute
                      <input
                        type="number"
                        min="1"
                        max="10000"
                        value={selectedPolicy.requestsPerMinute}
                        onChange={(event) => updatePolicy({ requestsPerMinute: Number(event.target.value) })}
                      />
                    </label>
                    <label className="field">
                      Requests per day
                      <input
                        type="number"
                        min="1"
                        max="100000"
                        value={selectedPolicy.requestsPerDay}
                        onChange={(event) => updatePolicy({ requestsPerDay: Number(event.target.value) })}
                      />
                    </label>
                    <label className="field">
                      Maximum prompt characters
                      <input
                        type="number"
                        min="1"
                        max="100000"
                        value={selectedPolicy.maxPromptChars}
                        onChange={(event) => updatePolicy({ maxPromptChars: Number(event.target.value) })}
                      />
                    </label>
                    <label className="field">
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
                  <label className="field">
                    Blocked patterns, one per line
                    <textarea
                      className="textarea-code"
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
                  <div className="checkbox-grid">
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={selectedPolicy.redactPii}
                        onChange={(event) => updatePolicy({ redactPii: event.target.checked })}
                      />
                      Redact personal data
                    </label>
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={selectedPolicy.storeContent}
                        onChange={(event) => updatePolicy({ storeContent: event.target.checked })}
                      />
                      Store prompt and response content
                    </label>
                    <label className="checkbox">
                      <input
                        type="checkbox"
                        checked={selectedPolicy.isEnabled}
                        onChange={(event) => updatePolicy({ isEnabled: event.target.checked })}
                      />
                      Policy enabled
                    </label>
                  </div>
                  {selectedPolicy.storeContent && (
                    <Alert tone="warning" role="note">
                      Content storage retains full prompts and responses, which may contain sensitive information.
                    </Alert>
                  )}
                  <div className="form-actions">
                    <button
                      className="btn btn-primary"
                      type="button"
                      disabled={saving}
                      onClick={() => void savePolicy()}
                    >
                      {saving ? "Saving…" : "Save AI policy"}
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </section>
    </div>
  );
}
