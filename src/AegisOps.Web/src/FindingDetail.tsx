import { useState, type ReactNode } from "react";
import { api } from "./api";

export type Finding = {
  id: string;
  artifactId: string;
  artifactVersion: string;
  scanId: string;
  scanner: string;
  severity: string;
  ruleId: string;
  title: string;
  description?: string | null;
  filePath?: string | null;
  startLine?: number | null;
  packageName?: string | null;
  installedVersion?: string | null;
  fixedVersion?: string | null;
  cve?: string | null;
  helpUri?: string | null;
  fingerprint: string;
  status: string;
  suppressionReason?: string | null;
  suppression?: { reason: string; createdAt: string; expiresAt?: string | null } | null;
};

/** Only http(s) help links are rendered as links; anything else (e.g. javascript:) is shown as text. */
function safeHelpUrl(value: string | null | undefined): string | null {
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:" ? url.href : null;
  } catch {
    return null;
  }
}

function location(finding: Finding): string | null {
  if (!finding.filePath) return null;
  return finding.startLine ? `${finding.filePath}:${finding.startLine}` : finding.filePath;
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </div>
  );
}

export function FindingDetail({
  token,
  projectSlug,
  finding,
  canSuppress,
  onClose,
  onSuppressed,
}: {
  token: string;
  projectSlug: string;
  finding: Finding;
  canSuppress: boolean;
  onClose: () => void;
  onSuppressed: (message: string) => void;
}) {
  const [reason, setReason] = useState("");
  const [expiresAt, setExpiresAt] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const helpUrl = safeHelpUrl(finding.helpUri);
  const fileLocation = location(finding);

  async function suppress() {
    if (!reason.trim() || saving) return;
    setSaving(true);
    setError(null);
    try {
      await api(`/api/v1/projects/${encodeURIComponent(projectSlug)}/finding-suppressions`, token, {
        method: "POST",
        body: JSON.stringify({
          fingerprint: finding.fingerprint,
          reason: reason.trim(),
          expiresAt: expiresAt ? new Date(expiresAt).toISOString() : null,
        }),
      });
      setReason("");
      setExpiresAt("");
      onSuppressed(`Suppressed ${finding.ruleId} for project ${projectSlug}.`);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : "Could not suppress the finding.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="panel finding-detail" aria-labelledby="finding-detail-heading">
      <div className="panel-heading">
        <h2 id="finding-detail-heading">{finding.title}</h2>
        <button className="secondary-button" type="button" onClick={onClose}>
          Back to findings
        </button>
      </div>
      <p className="finding-badges">
        <span className={`severity-badge severity-${finding.severity.toLowerCase()}`}>{finding.severity}</span>
        <span className="finding-status">{finding.status}</span>
        <span>{finding.scanner}</span>
      </p>
      <dl className="governance-list finding-fields">
        <Field label="Rule">{finding.ruleId}</Field>
        <Field label="Description">
          <span className="finding-description">{finding.description || "No description provided."}</span>
        </Field>
        <Field label="Location">{fileLocation ?? "—"}</Field>
        <Field label="Package">
          {finding.packageName
            ? `${finding.packageName} ${finding.installedVersion ?? ""}`.trim() +
              (finding.fixedVersion ? ` → fixed in ${finding.fixedVersion}` : " · no fix version reported")
            : "—"}
        </Field>
        <Field label="CVE">{finding.cve ?? "—"}</Field>
        <Field label="Help">
          {helpUrl ? (
            <a href={helpUrl} target="_blank" rel="noopener noreferrer">
              {helpUrl}
            </a>
          ) : (
            (finding.helpUri ?? "—")
          )}
        </Field>
        <Field label="Artifact">{finding.artifactVersion}</Field>
        <Field label="Fingerprint">
          <code className="governance-id">{finding.fingerprint}</code>
        </Field>
        <Field label="Suppression">
          {finding.suppression ? (
            <span data-testid="suppression-info">
              {finding.suppression.reason} · since {new Date(finding.suppression.createdAt).toLocaleString()} ·{" "}
              {finding.suppression.expiresAt
                ? `expires ${new Date(finding.suppression.expiresAt).toLocaleString()}`
                : "no expiry"}
            </span>
          ) : finding.suppressionReason ? (
            <span data-testid="suppression-info">{finding.suppressionReason}</span>
          ) : (
            "Not suppressed"
          )}
        </Field>
      </dl>

      {canSuppress && (
        <form
          className="suppress-form"
          aria-label="Suppress finding"
          onSubmit={(event) => {
            event.preventDefault();
            void suppress();
          }}
        >
          <h3 className="subheading">Suppress by fingerprint</h3>
          <p className="empty-state">
            Suppression applies to this fingerprint in every artifact of {projectSlug}, including later scans.
          </p>
          <label className="field-label">
            Reason (required)
            <textarea rows={3} value={reason} required onChange={(event) => setReason(event.target.value)} />
          </label>
          <label className="field-label">
            Expires (optional)
            <input type="datetime-local" value={expiresAt} onChange={(event) => setExpiresAt(event.target.value)} />
          </label>
          {error && (
            <p className="ai-error" role="alert">
              {error}
            </p>
          )}
          <button className="primary-button" type="submit" disabled={!reason.trim() || saving} aria-busy={saving}>
            {saving ? "Suppressing…" : "Suppress"}
          </button>
        </form>
      )}
    </section>
  );
}
