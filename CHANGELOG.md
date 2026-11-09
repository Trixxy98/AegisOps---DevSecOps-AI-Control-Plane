# Changelog

All notable changes to AegisOps are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). Entries are curated from the
Conventional Commit history (`git log --no-merges`).

## [Unreleased] — towards v1.0.0

### Added

- **Control plane (Phase 1):** projects, repositories, environments with noop/Docker
  targets, project-scoped API keys (create/list/revoke), artifact registration with
  digest backfill and CI pre-release versions, policies with editable rule parameters,
  and audit events for every Phase 1 change.
- **Deployment pipeline:** a policy engine (`Allow` / `RequireApproval` / `Deny`), a
  durable PostgreSQL job queue with leased, serialized Worker processing across
  replicas, approval re-checks before execution, and Docker execution that replaces the
  target container, health-checks it, and rolls back on failure.
- **CI integration (Phase 2):** target-repository workflows that register artifacts,
  upload scan reports and request Staging deployments; `scripts/ci-replay.sh` with saved
  payloads for offline replay.
- **Security scanning (Phase 3):** multipart SARIF ingestion (Gitleaks, Trivy, Semgrep),
  inline parsing up to 2 MiB with an async parse job above that, fingerprint
  suppressions, and scan gates in policy evaluation.
- **Real-time updates (Phase 5):** SignalR deployment timelines and approval toasts with
  a polling fallback, Redis pub/sub hints from the Worker, and opt-in signed team
  webhooks with encrypted secrets, SSRF-safe delivery and attempt history.
- **AI gateway (Phase 6):** a governed Ollama gateway with per-team policies, model
  allow-lists, Redis rate limits, sensitive-data detection with redact/block, response
  redaction, governed deployment summaries and finding explanations, and an AI request
  log in the UI.
- **Observability (Phase 7):** API/Worker readiness with degraded Ollama/Docker states
  shown in the UI; Prometheus metrics for HTTP requests, jobs, queue depth and AI
  outcomes; an opt-in Prometheus + Grafana stack with three provisioned dashboards and
  alert rules; bounded OpenTelemetry tracing with job-to-request links.
- **Hardening and operations (Phase 8):** SPA CSP and security headers; Worker retention
  services that purge succeeded jobs after seven days and expired or orphaned scan
  reports; an opt-in `ai` Compose profile (`ollama`, `ollama-init`); named Staging and
  Production target networks; Worker `group_add` for the Docker socket;
  `scripts/dev-up.sh`, `scripts/dev-down.sh` and `scripts/gen-secrets.sh`; Dependabot.

### Fixed

- Deployment authorization is enforced per project and for Production.
- Matching artifacts are reused safely on CI replay, and CI reports are validated before
  registration.
- Scan reports are shared between the API and the Worker.
- AI gateway: one enabled policy per scope, oversized prompts rejected before
  inspection, provider responses redacted, inspection failures classified.
- The API key parser no longer depends on the random secret's characters.
- Trace headers are no longer forwarded to third-party webhooks.
- Scan uploads over 20 MB return 400 instead of 500.
- Re-suppressing an expired finding fingerprint renews it for later scans.
- Team Owners (not only Admins) can manage their own team and members, as documented.
