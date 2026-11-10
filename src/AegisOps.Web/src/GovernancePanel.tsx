export type RateLimitRemaining = { remainingMinute: number; remainingDay: number };

/** Governance metadata for the most recent AI playground request, allowed or blocked. */
export type GovernanceState = {
  outcome: "Allowed" | "Blocked" | "Failed";
  status: number;
  requestId?: string;
  model?: string;
  policyId?: string | null;
  action?: string;
  detected: string[];
  redactions: number | null;
  blockReason?: string;
  rateLimit: RateLimitRemaining | null;
  rateLimited?: boolean;
};

export function GovernancePanel({ governance }: { governance: GovernanceState | null }) {
  return (
    <section className="panel governance-panel" aria-labelledby="governance-heading">
      <div className="panel-heading">
        <h2 id="governance-heading">Governance</h2>
        {governance && (
          <span className={`ai-status ai-status-${governance.outcome === "Allowed" ? "completed" : "blocked"}`}>
            {governance.outcome}
          </span>
        )}
      </div>
      {!governance ? (
        <p className="empty-state">Send a prompt to see how the AI policy handled it.</p>
      ) : (
        <dl className="governance-list">
          <div>
            <dt>HTTP status</dt>
            <dd>{governance.status}</dd>
          </div>
          {governance.model && (
            <div>
              <dt>Model</dt>
              <dd>{governance.model}</dd>
            </div>
          )}
          <div>
            <dt>Policy action</dt>
            <dd data-testid="governance-action">{governance.action ?? "—"}</dd>
          </div>
          <div>
            <dt>Policy</dt>
            <dd>{governance.policyId ?? "Not reported"}</dd>
          </div>
          <div>
            <dt>Detected sensitive types</dt>
            <dd data-testid="governance-detected">
              {governance.detected.length
                ? governance.detected.join(", ")
                : governance.outcome === "Allowed"
                  ? "None"
                  : "Not reported"}
            </dd>
          </div>
          <div>
            <dt>Redactions applied</dt>
            <dd data-testid="governance-redactions">{governance.redactions ?? "Not reported"}</dd>
          </div>
          <div>
            <dt>Block reason</dt>
            <dd data-testid="governance-block-reason">{governance.blockReason ?? "None"}</dd>
          </div>
          <div>
            <dt>Rate limit remaining</dt>
            <dd data-testid="governance-rate-limit">
              {governance.rateLimited
                ? "Limit reached"
                : governance.rateLimit
                  ? `${governance.rateLimit.remainingMinute} this minute · ${governance.rateLimit.remainingDay} today`
                  : "Not reported"}
            </dd>
          </div>
          {governance.requestId && (
            <div>
              <dt>Request ID</dt>
              <dd className="governance-id">{governance.requestId}</dd>
            </div>
          )}
        </dl>
      )}
    </section>
  );
}
