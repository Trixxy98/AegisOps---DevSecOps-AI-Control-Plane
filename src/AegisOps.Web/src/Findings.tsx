import { useEffect, useState } from "react";
import { api } from "./api";
import { FindingDetail, type Finding } from "./FindingDetail";
import { IconChevronLeft, IconChevronRight, IconFindings } from "./icons";
import { Alert, CardHeader, EmptyState, Loading, SeverityBadge, StatusBadge } from "./ui";

type Project = { slug: string; name: string };
type ArtifactOption = { id: string; version: string; branch: string };
type FindingPage = { page: number; pageSize: number; total: number; items: Finding[] };
type Query = { slug: string; severity: string; scanner: string; status: string; artifactId: string; page: number };

// The explorer always asks for a bounded page; the API caps pageSize at 100.
const pageSize = 25;
const severities = ["Critical", "High", "Medium", "Low", "Info", "Unknown"];
const scanners = ["Gitleaks", "Trivy", "Semgrep"];
const statuses = ["Open", "Suppressed", "Resolved"];

function findingsPath(query: Query): string {
  const params = new URLSearchParams({ page: String(query.page), pageSize: String(pageSize) });
  if (query.severity) params.set("severity", query.severity);
  if (query.scanner) params.set("scanner", query.scanner);
  if (query.status) params.set("status", query.status);
  if (query.artifactId) params.set("artifactId", query.artifactId);
  return `/api/v1/projects/${encodeURIComponent(query.slug)}/findings?${params.toString()}`;
}

export function Findings({ token, permissions }: { token: string; permissions: string[] }) {
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectsLoading, setProjectsLoading] = useState(true);
  const [artifacts, setArtifacts] = useState<ArtifactOption[]>([]);
  const [query, setQuery] = useState<Query>({
    slug: "",
    severity: "",
    scanner: "",
    status: "",
    artifactId: "",
    page: 1,
  });
  const [reload, setReload] = useState(0);
  const [result, setResult] = useState<FindingPage | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const canSuppress = permissions.includes("findings:suppress");

  useEffect(() => {
    let active = true;
    api<Project[]>("/api/v1/projects", token)
      .then((items) => {
        if (!active) return;
        setProjects(items);
        if (items[0]) {
          setLoading(true);
          setQuery((current) => (current.slug ? current : { ...current, slug: items[0].slug }));
        }
      })
      .catch((caught: Error) => {
        if (active) setError(caught.message);
      })
      .finally(() => {
        if (active) setProjectsLoading(false);
      });
    return () => {
      active = false;
    };
  }, [token]);

  useEffect(() => {
    if (!query.slug) return;
    let active = true;
    api<ArtifactOption[]>(`/api/v1/projects/${encodeURIComponent(query.slug)}/artifacts`, token)
      .then((items) => {
        if (active) setArtifacts(items);
      })
      .catch(() => {
        if (active) setArtifacts([]);
      });
    return () => {
      active = false;
    };
  }, [query.slug, token]);

  useEffect(() => {
    if (!query.slug) return;
    let active = true;
    api<FindingPage>(findingsPath(query), token)
      .then((page) => {
        if (active) setResult(page);
      })
      .catch((caught: Error) => {
        if (!active) return;
        setResult(null);
        setError(caught.message);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, [query, reload, token]);

  function update(patch: Partial<Query>) {
    setLoading(true);
    setError(null);
    setMessage(null);
    setSelectedId(null);
    setQuery((current) => ({
      ...current,
      ...(patch.slug !== undefined && patch.slug !== current.slug ? { artifactId: "" } : {}),
      page: 1,
      ...patch,
    }));
  }

  const items = result?.items ?? [];
  const selected = items.find((item) => item.id === selectedId) ?? null;
  const pageCount = Math.max(1, Math.ceil((result?.total ?? 0) / pageSize));

  if (!projectsLoading && !projects.length && !error) {
    return (
      <section className="card">
        <EmptyState icon={<IconFindings />}>No projects yet. Findings appear once CI uploads scan results.</EmptyState>
      </section>
    );
  }

  return (
    <div className={selected ? "split split-detail" : "stack"}>
      <section className="card" aria-labelledby="findings-heading">
        <CardHeader
          id="findings-heading"
          title="Findings"
          meta={
            <>
              {result?.total ?? 0} total · page {query.page} of {pageCount}
            </>
          }
        />
        <div className="filters">
          <label className="field">
            Project
            <select value={query.slug} onChange={(event) => update({ slug: event.target.value })}>
              {projects.map((project) => (
                <option key={project.slug} value={project.slug}>
                  {project.name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            Artifact
            <select value={query.artifactId} onChange={(event) => update({ artifactId: event.target.value })}>
              <option value="">All artifacts</option>
              {artifacts.map((artifact) => (
                <option key={artifact.id} value={artifact.id}>
                  {artifact.version} · {artifact.branch}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            Severity
            <select value={query.severity} onChange={(event) => update({ severity: event.target.value })}>
              <option value="">All severities</option>
              {severities.map((value) => (
                <option key={value}>{value}</option>
              ))}
            </select>
          </label>
          <label className="field">
            Scanner
            <select value={query.scanner} onChange={(event) => update({ scanner: event.target.value })}>
              <option value="">All scanners</option>
              {scanners.map((value) => (
                <option key={value}>{value}</option>
              ))}
            </select>
          </label>
          <label className="field">
            Status
            <select value={query.status} onChange={(event) => update({ status: event.target.value })}>
              <option value="">All statuses</option>
              {statuses.map((value) => (
                <option key={value}>{value}</option>
              ))}
            </select>
          </label>
        </div>
        {(error || message) && (
          <div className="card-body">
            {error && <Alert>{error}</Alert>}
            {message && (
              <Alert tone="success" role="status">
                {message}
              </Alert>
            )}
          </div>
        )}
        {loading || projectsLoading ? (
          <Loading>Loading findings…</Loading>
        ) : (
          <ul className="list findings-list" aria-label="Findings list">
            {items.map((finding) => (
              <li key={finding.id}>
                <button
                  type="button"
                  className={`list-button finding-button ${selectedId === finding.id ? "is-selected" : ""}`}
                  onClick={() => setSelectedId(finding.id)}
                >
                  <SeverityBadge severity={finding.severity} />
                  <span className="finding-text">
                    <span className="list-primary">
                      <span className="text">{finding.title}</span>
                    </span>
                    <span className="list-secondary">
                      <code className="mono">{finding.ruleId}</code>
                      <span className="dot-sep">{finding.scanner}</span>
                      <span className="dot-sep">{finding.artifactVersion}</span>
                    </span>
                  </span>
                  <StatusBadge status={finding.status} />
                </button>
              </li>
            ))}
            {!items.length && <li className="empty-row">No findings match these filters.</li>}
          </ul>
        )}
        <div className="card-footer">
          <span className="tabular">
            Page {query.page} of {pageCount}
          </span>
          <span className="row">
            <button
              className="btn btn-secondary btn-sm"
              type="button"
              disabled={query.page <= 1 || loading}
              onClick={() => update({ page: query.page - 1 })}
            >
              <IconChevronLeft size={14} />
              Previous
            </button>
            <button
              className="btn btn-secondary btn-sm"
              type="button"
              disabled={query.page >= pageCount || loading}
              onClick={() => update({ page: query.page + 1 })}
            >
              Next
              <IconChevronRight size={14} />
            </button>
          </span>
        </div>
      </section>
      {selected && (
        <FindingDetail
          key={selected.id}
          token={token}
          projectSlug={query.slug}
          finding={selected}
          canSuppress={canSuppress}
          onClose={() => setSelectedId(null)}
          onSuppressed={(text) => {
            // Re-read the list so status and suppression details come from the API, not an optimistic update.
            setMessage(text);
            setLoading(true);
            setReload((value) => value + 1);
          }}
        />
      )}
    </div>
  );
}
