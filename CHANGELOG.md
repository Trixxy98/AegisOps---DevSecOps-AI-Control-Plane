# Changelog

All notable changes to AegisOps are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). Entries are curated from the
Conventional Commit history (`git log --no-merges`).

## [Unreleased]

### Security

- API and Worker containers run as the unprivileged `app` user (UID 1654) instead of root.
- nginx forwards only `websocket` upgrades to the API (no h2c upgrade smuggling) and the CSP allows
  `connect-src 'self'` only (no unencrypted WebSocket origin, no host taken from the request).
- GitHub Actions are pinned by commit SHA; Dependabot waits 7 days before proposing a new release, groups
  `react`/`react-dom`, and keeps nginx on its stable line.

### Changed

- `react` and `react-dom` 19.3.1.

## [1.0.0] — 2026-11-11

First public release, for evaluation and demos on a single machine (see README, "Intended use and limits").

### Added

- **First sign-in password change:** accounts with a temporary password (seeded or Admin-reset) must choose their
  own before using AegisOps; the API refuses other routes (`PasswordChangeRequired`) and the SPA guides the change.
- **Open-source project files:** `SECURITY.md` (private vulnerability reporting), `CONTRIBUTING.md`, issue
  templates, an "Intended use and limits" README section and the v1.1 production-readiness roadmap.
- **Demo projects in the seed:** a fresh Development stack now has `circuit-service`, `site-service` and
  `notification-service` (Development/Staging/Production) in the `Network Platform` team, with security and
  approver as members, so docs/demo-script.md works from a fresh clone.
- **Local tracing:** `deploy/docker-compose.tracing.yml` adds Jaeger and points the API and Worker at it over
  OTLP; `scripts/trace-check.sh` reports which use-case, policy, scan, Ollama and Docker spans arrived.
- **Release workflow:** a `v*.*.*` tag builds the API, Worker and web images, blocks images with fixable
  CRITICAL vulnerabilities (Trivy image scan), publishes them to GHCR and creates the GitHub Release.
- **Target containers restart after a Docker restart:** the executor creates them with
  `restart: unless-stopped` (a stopped `-previous` backup stays stopped).
- **Live realtime check:** `npm run test:e2e:live` (opt-in, never in CI) drives two real browser
  sessions against the Compose stack and asserts the approver toast arrives within one second
  of the Worker's AwaitingApproval commit, then cancels the deployment it created.
- **Policy simulation at a chosen time:** `POST /api/v1/policies/simulate` accepts an optional
  `at` instant for time-based rules (Journey D's Friday-night blackout); `scripts/journey-d.sh`
  runs that check read-only.
- **Admin password reset:** `POST /api/v1/admin/users/{id}/reset-password` sets a random
  temporary password returned once, requires a change on next sign-in, ends the user's
  sessions, and is audited as `user.password_reset` (docs/09). `scripts/demo-reset-passwords.sh`
  uses it for the demo accounts.
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

- **Security review (docs/security-review-v1.md):** Docker targets can no longer join the control-plane
  network (only `aegisops-<name>` networks, validated names, host ports ≥ 1024, re-checked by the executor);
  login now enforces the 5-failure / 15-minute lockout; stored AI prompt content is the redacted prompt;
  Compose publishes PostgreSQL, Redis, API and web on `127.0.0.1` only.
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
