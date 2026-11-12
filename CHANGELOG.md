# Changelog

All notable changes to AegisOps are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/). Entries are curated from the
Conventional Commit history (`git log --no-merges`).

## [Unreleased]

### Added

- Production profile can run the published release images: `AEGISOPS_IMAGE_PREFIX` and `AEGISOPS_IMAGE_TAG`
  (docs/deploy-production.md §5).

### Changed

- README shortened, with light/dark screenshots of the new UI; local development details moved to
  `docs/local-development.md`. The old demo GIF and screenshots were removed (re-record with
  `scripts/make-demo-gif.sh`).
- Redesigned web UI with light and dark themes: design tokens, a System/Light/Dark switch in the sidebar
  (stored only as a preference, applied before first paint), grouped navigation with icons and an approvals
  count, one live-connection indicator, tables for lists, consistent status badges, alerts, empty states and
  buttons, and a minimal sign-in page. Visible labels changed: the connection indicator shows "Live" /
  "Reconnecting…", statuses are shown as words ("Awaiting approval"), and Administration has Users, Teams and
  Jobs tabs. API calls, permissions and live-update rules are unchanged. `e2e/visual-tour.spec.ts` checks every
  page in both themes. The README demo GIF predates this design.
- Release and example workflows use the current Docker actions (setup-qemu v4.4.0, setup-buildx v4.4.1,
  build-push v7.4.0, login v4.8.0; Node 24, self-hosted runners need 2.327.1 or later). Inputs are unchanged.
- Dependabot groups `eslint` with `@eslint/js` and defers ESLint 10 until `eslint-plugin-jsx-a11y` supports it.

## [1.1.0] — 2026-11-12

Production readiness for a single server: environment secrets, self-service onboarding, a production
profile with HTTPS, user management, per-principal rate limits, an audit-restricted database role,
backup and restore, hardened regexes, encrypted AI content and multi-arch release images.

### Added

- Production profile (roadmap v1.1 item 1): `deploy/docker-compose.prod.yml` runs api and worker as `Production`
  without automatic migrations, unpublishes PostgreSQL and Redis, adds restart policies and a one-shot `migrate`
  service. The API image has operator commands: `migrate` (EF migrations plus the Admin/Developer/Security/Approver
  roles, idempotent) and `bootstrap-admin` (below). The Development auto-migrate and demo seed are unchanged.
- First-admin bootstrap (item 2): `bootstrap-admin --email <address> [--display-name <name>]` creates the first Admin
  with a random temporary password printed once and a forced change at first sign-in, audited as
  `user.bootstrapped`; it refuses when any Admin exists.
- User management (item 2 follow-up): `POST /api/v1/admin/users` creates a user with roles, an optional team
  membership and a random temporary password returned once (`Cache-Control: no-store`, forced change at first
  sign-in); `PATCH /api/v1/admin/users/{id}` renames and activates/deactivates (revoking refresh tokens);
  `PUT /api/v1/admin/users/{id}/roles` replaces roles (`roles:manage`, revokes refresh tokens). Guard rails: no
  self-deactivation or removal of one's own Admin role, and the last active Admin is kept (409). Audited as
  `user.created`, `user.updated`, `user.deactivated`, `user.activated`, `user.roles_changed`, never with the
  password. Administration → Users gets an Add user form (password shown once), Activate/Deactivate, Edit roles
  and Reset password.
- HTTPS (item 3): `deploy/docker-compose.tls.yml` and `deploy/caddy/Caddyfile` put Caddy in front (automatic
  certificates for `AEGISOPS_DOMAIN`, HTTP→HTTPS, HSTS); only ports 80/443 are published in that mode.
- Unhandled API exceptions return RFC 9457 Problem Details with a `traceId` (item 5).
- Backup and restore (item 9): `scripts/backup.sh [--output DIR] [--project-name NAME] [--prod] [--tls] [--dry-run]`
  writes `aegisops-<UTC time>/` with a `pg_dump` custom-format dump, a tarball of the `reports` volume (read-only
  mount), the applied migrations, per-table row counts, `manifest.json` and `checksums.sha256` (private files; `.env`
  is never read or copied). `scripts/restore.sh BACKUP_DIR [--project-name NAME] [--prod] [--tls] [--yes]
  [--dry-run]` verifies the checksums, asks for a typed `restore`, stops api/worker/web, restores into a temporary
  database that replaces the current one only after `pg_restore` and the row-count check succeed, restores the
  reports volume, runs `migrate` in production and waits for `/health/ready`; `--verify-only` restores into a
  throw-away PostgreSQL container instead. `deploy/docker-compose.drill.yml` runs an isolated restore drill next to
  the real stack (own network, other loopback ports, no Worker).
- Compose passes `ENVIRONMENT_SECRETS_KEY_2` to api and worker (`EnvironmentSecrets__Keys__key2`) so the environment
  secret key can be rotated without editing `docker-compose.yml`.

- Environment configuration and secrets: Owners and Admins set per-environment variables
  (`GET/PUT/DELETE /api/v1/projects/{slug}/environments/{envId}/variables[/{name}]`) that the Worker injects into
  the deployed container next to `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT`. Secret values are AES-256-GCM
  encrypted with the new `EnvironmentSecrets` key ring (bound to the environment and name), write-only in the API
  and UI, and never written to audit rows or logs. New table `org.environment_variables` (migration
  `EnvironmentVariables`); `scripts/gen-secrets.sh` now generates `ENVIRONMENT_SECRETS_*`; Projects screen shows
  and edits variables per environment.
- Self-service onboarding: a **Create project** form on the Projects page (team, name, slug, description) for
  Admins and team Owners, using the existing `POST /api/v1/projects`; the new project is selected after creation
  and API Problem Details are shown inline.
- `examples/github-actions/`: a copy-paste GitHub Actions workflow (build/test, GHCR image with a unique version
  per run, Gitleaks/Semgrep/Trivy SARIF, report job on a self-hosted runner) with `image-meta.sh`,
  `verify-scans.sh` and `test-summary.sh` (JUnit XML to the AegisOps test summary).

### Documentation

- `docs/operations.md` (item 9): what AegisOps state consists of, backups (schedule, off-host copies, encryption
  at rest with age/gpg, retention, cron), restore and a quarterly restore drill, upgrades and rollback, the
  forward-only migration policy, rotating the JWT key, the webhook and environment-secret key rings, database
  passwords and API keys, health, logs and disk housekeeping, sizing estimates and troubleshooting. Linked from the
  README, docs index, deploy-production guide and user manual; deploy-production's backup section now uses the
  scripts.
- `docs/deploy-production.md`: beginner-friendly guide for one server with HTTPS (requirements, DNS, `.env`,
  `migrate`, `bootstrap-admin`, start, first steps, backups, upgrades, troubleshooting, what is not covered).
  README limits, docs/14, docs/17, the user manual and roadmap v1.1 updated accordingly.
- `docs/connect-your-repository.md`: end-to-end guide for connecting your own repository (prerequisites,
  project and Staging target, API key, GitHub variables/secret/self-hosted runner, full workflow, plain-curl calls
  for other CI systems, what happens next, troubleshooting). `.github/actions/aegisops-report/README.md`
  documents the action's inputs, API key scopes, exit behaviour and `@v1` versioning.
- `docs/user-manual.md`: a beginner's guide with plain-language concepts, a glossary, installation, a
  screen-by-screen tour, four hands-on tutorials, AI usage, roles and troubleshooting.

### Security

- Outside Development the API refuses to start with the `.env.example` JWT placeholder, a missing JWT key or the
  Development database password.
- Opt-in forwarded headers (`ForwardedHeaders:Enabled`, `KnownNetworks`/`KnownProxies`, `ForwardLimit`): the API
  honours `X-Forwarded-For`/`-Proto` only from listed proxies, before logging, authentication and the auth rate
  limiter, so limits and refresh-token records see the real client behind Caddy and nginx. nginx now forwards both
  headers on `/api/` and `/hubs/` and accepts scan uploads up to the API's 20 MiB limit.
- Exception details are never returned to clients; the generic 500 response carries only a `traceId`.
- Rate limits per principal (roadmap v1.1 item 4): a global token bucket on `/api` per user (`sub`), API key or,
  for anonymous requests, client IP (300 tokens, 50 back every 10 s), a separate bucket on `/hubs`, an `uploads`
  policy for `POST /api/v1/artifacts/{id}/scans` (30, 5 every 10 s) and the unchanged `auth` policy (10/min per
  IP). Health and metrics are never limited. Every 429 is RFC 9457 Problem Details with `Retry-After` (the auth
  limiter sent none before). Configurable under `RateLimits:*`. The limiter now runs right after authentication.
- Audit tamper protection (item 6): the `migrate` command creates or updates the LOGIN role `aegisops_app` from
  `AppDatabase:Password` (`APP_DB_PASSWORD`) with normal rights on the application schemas but only
  `SELECT`/`INSERT` on `audit.*`, including default privileges for future tables. In
  `deploy/docker-compose.prod.yml` the api and worker connect as `aegisops_app` and only `migrate` as the owner.
  `scripts/gen-secrets.sh` generates `APP_DB_PASSWORD`; `--upgrade` appends it (and only it) to an existing `.env`,
  adding a missing final newline first. **Upgrade:** run `scripts/gen-secrets.sh --upgrade`, `migrate`, then
  `up -d`. The Development stack is unchanged (owner connection).
- Regex hardening (item 7): policy branch globs and AI `BlockedPatterns` are compiled with
  `RegexOptions.NonBacktracking` (linear-time matching) and a 100 ms timeout. Both are validated when the policy is
  saved: an `AllowedBranches` rule needs 1–50 non-empty globs of at most 255 characters without control characters,
  and blocked patterns must compile without constructs NonBacktracking does not support (backreferences,
  lookarounds, atomic groups, conditionals); violations return 400 Problem Details naming the problem. Evaluation
  fails closed: an AI pattern that times out or no longer compiles (stored before this change) blocks the request
  with 503 `PolicyInvalid`; a branch rule that cannot be evaluated denies.
- Stored AI content encrypted at rest (item 7): `ai.ai_requests.prompt_content`/`response_content` hold only
  AES-256-GCM envelopes from the new `AiContent` key ring (`Enabled`, `CurrentKeyId`, `Keys:<id>`; Compose
  `AI_CONTENT_*` on the api), bound to the request ID and column. With `StoreContent` on but no key, nothing is
  stored and the new column `content_skipped` is set (migration `AiContentSkipped`); a ring enabled without its
  current key stops the API at startup. `scripts/gen-secrets.sh` generates the key, and `--upgrade` now also adds
  `AI_CONTENT_*` when missing. **Upgrade:** content stored as plaintext before this release is not re-encrypted; it
  expires with retention, or can be cleared at once (docs/operations.md §5.6).
- CI scope (item 10): Semgrep also runs `p/owasp-top-ten` and the Security job fails on ERROR-severity findings
  (all findings still go to SARIF); the Frontend job runs `npm audit --audit-level=high --omit=dev`; the Backend job
  lists vulnerable NuGet packages (`dotnet list package --vulnerable --include-transitive`) and fails on any.

- API and Worker containers run as the unprivileged `app` user (UID 1654) instead of root.
- nginx forwards only `websocket` upgrades to the API (no h2c upgrade smuggling) and the CSP allows
  `connect-src 'self'` only (no unencrypted WebSocket origin, no host taken from the request).
- GitHub Actions are pinned by commit SHA; Dependabot waits 7 days before proposing a new release, groups
  `react`/`react-dom`, and keeps nginx on its stable line.

### Changed

- `react` and `react-dom` 19.3.1.
- Release images are multi-arch (linux/amd64 + linux/arm64, item 10): the Trivy CRITICAL gate scans the amd64 image
  before anything is pushed. The API, Worker and Web Dockerfiles run their build stage on `$BUILDPLATFORM`
  (architecture-neutral output), so only the runtime stages are emulated.

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
