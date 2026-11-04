# v1 progress audit

Audit baseline: 2026-11-04, starting commit `bbf2a54` on `dev`; this refreshed estimate includes the local, uncommitted roadmap and `IAuditWriter` changes.

Per `19-roadmap.md`, a check receives credit only when its implementation and the stated passing verification evidence are both established. All Phases 0–8 are enumerated below; unresolved scope mismatches are conservatively counted as zero.

Status meanings:
- Verified: implementation and the stated local verification are evidenced below.
- Unverified: not enough runtime/CI/demo evidence; earns no credit yet.
- Scope mismatch: roadmap criterion conflicts with the repository; resolve before scoring.
- Missing: the named deliverable was not found in the tracked repository.

## Phase 0 — Foundation & scaffolding (weight: 1 / 15.5 weeks)

| ID | Atomic acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P0-01 | `.gitignore` is present | Verified | Tracked at repository root. |
| P0-02 | MIT `LICENSE` is present | Verified | Tracked at repository root. |
| P0-03 | `README.md` and project docs are present | Verified | Both tracked. |
| P0-04 | Domain project is in the solution | Verified | Listed in `AegisOps.slnx`. |
| P0-05 | Application project is in the solution | Verified | Listed in `AegisOps.slnx`. |
| P0-06 | Infrastructure project is in the solution | Verified | Listed in `AegisOps.slnx`. |
| P0-07 | API project is in the solution | Verified | Listed in `AegisOps.slnx`. |
| P0-08 | Worker project is in the solution | Verified | Listed in `AegisOps.slnx`. |
| P0-09 | Three test projects are in the solution | Verified | `AegisOps.slnx` lists the three agreed test projects; the full solution suite passed in the audit baseline. |
| P0-10 | Architecture dependency tests exist and pass | Verified | `tests/AegisOps.Architecture.Tests`; latest full solution test run passed. |
| P0-11 | React, Vite, and TypeScript frontend scaffold exists | Verified | Dependencies/configuration and source are tracked. |
| P0-12 | Tailwind frontend integration exists | Verified | `@tailwindcss/vite` is configured in the frontend package. |
| P0-13 | App shell includes sidebar and login placeholder | Verified | App source contains sidebar and login UI; production build passes. |
| P0-14 | Frontend typecheck and production build pass | Verified | `npm run build` passed (TypeScript + Vite). |
| P0-15 | ESLint is configured | Missing | No ESLint dependency or lint script in frontend `package.json`. |
| P0-16 | Prettier is configured | Missing | No Prettier dependency or format script in frontend `package.json`. |
| P0-17 | Compose defines PostgreSQL, Redis, API, Worker, and web services | Verified | All five services are in `docker-compose.yml`. |
| P0-18 | Compose services build/start and API readiness is healthy | Unverified | Docker daemon is unavailable, so runtime health was not verified. |
| P0-19 | CI workflow configures backend restore/build/test and report-contract checks | Verified | Steps are present in `.github/workflows/ci.yml`; hosted run status is tracked separately. |
| P0-20 | CI workflow configures frontend build and browser E2E | Verified | Steps are present in `.github/workflows/ci.yml`; hosted run status is tracked separately. |
| P0-21 | CI workflow configures Gitleaks, Semgrep, and Trivy | Verified | Security job steps are present in `.github/workflows/ci.yml`. |
| P0-22 | `scripts/dev-up.sh` exists | Missing | Not tracked. |
| P0-23 | `gen-secrets.sh` exists | Missing | Not tracked at repository root or under `scripts/`. |
| P0-24 | `.env.example` exists | Verified | Tracked. |
| P0-25 | Dependabot configuration exists | Missing | No tracked `.github/dependabot.yml` or `.yaml` found. |
| P0-26 | Compose serves UI at `:3000` | Unverified | Requires Compose runtime, unavailable during this audit. |
| P0-27 | API `/health/ready` is healthy in Compose | Unverified | Requires Compose runtime, unavailable during this audit. |
| P0-28 | CI is green on `main` | Unverified | No successful main-branch CI run evidence collected. |

### Verification evidence

- `AEGISOPS_TEST_REDIS=localhost:6379 dotnet test AegisOps.slnx --no-restore`: 155 passed, 0 failed, 0 skipped.
- `npm run build` in `src/AegisOps.Web`: passed.
- Docker check earlier in this session: daemon unavailable; PostgreSQL port 5432 not listening. Redis is available locally and the focused Redis limiter integration test passed separately.

### Phase 0 remaining gaps

The roadmap has been rebaselined to the three test projects already in `AegisOps.slnx`. It still calls for `scripts/dev-up.sh`, `gen-secrets.sh`, Dependabot, ESLint, and Prettier, which are not tracked/configured.

## Phase 1 — Core control plane (weight: 3 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P1-01 | ASP.NET Identity and JWT authentication are registered | Verified | `Program.cs` and `JwtAuthenticationExtensions.cs`; solution test/build evidence passes. |
| P1-02 | Login endpoint issues an access token | Unverified | Endpoint is mapped and implementation exists; no dedicated login integration test was found. |
| P1-03 | Refresh tokens rotate and can be revoked | Unverified | `Refresh.cs`/`Logout.cs` implement rotation/revocation; no dedicated identity-flow test evidence found. |
| P1-04 | Roles and development demo users are seeded | Unverified | Seed implementation exists; seeded runtime state was not exercised against PostgreSQL. |
| P1-05 | Permission matrix is tested across roles and actions | Unverified | Deployment authorization tests cover important access paths, but no complete permission × role matrix test was found. |
| P1-06 | API-key domain model and authentication handler exist | Unverified | `ApiKey` and `ApiKeyAuthenticationHandler` exist; a real credential authentication test was not found. |
| P1-07 | Team/member APIs and membership scoping work | Unverified | Routes and implementation exist; deployment authorization tests cover some membership scoping, not the full organization API flow. |
| P1-08 | Project creation provisions Dev/Staging/Production environments | Unverified | `CreateProject` provisions the three environments; no executed API/database test was found. |
| P1-09 | Repository configuration and noop environment targets work | Unverified | Models and endpoints exist; no complete flow test/runtime verification found. |
| P1-10 | Artifact registration via UI/API, lists/details, and test summary work | Unverified | API/UI implementation exists and `ArtifactReplayTests` cover identity matching; no end-to-end artifact registration flow evidence found. |
| P1-11 | Policy entities and the six Phase 1 rule behaviors are implemented and tested | Verified | `PolicyEngineTests` covers tests, approvals, branch, prior environment, digest, and deployment window; full suite passed. |
| P1-12 | Rule-type catalogue and policy simulation endpoints work | Unverified | Routes and handlers exist; no endpoint-level verification was found. |
| P1-13 | Deployment state transitions, approval quorum, and cancellation are tested | Verified | `DeploymentStateTests` exercises transitions; `DeploymentAuthorizationTests` covers approval/cancellation authorization; full suite passed. |
| P1-14 | Request → durable PostgreSQL job → separate Worker `DeploymentJobService`/`DeploymentProcessor` → evaluate → noop execute → succeeded | Unverified | The API enqueues jobs and Worker code claims/processes them; PostgreSQL-backed end-to-end execution was not verified because PostgreSQL/Docker are unavailable. |
| P1-15 | Separation of duties is enforced for approvals | Verified | `DeploymentAuthorizationTests` covers requester/non-requester and role restrictions; full suite passed. |
| P1-16 | PostgreSQL jobs use `SKIP LOCKED`, retry/backoff, and stale-job recovery | Unverified | `DeploymentProcessor` uses `FOR UPDATE SKIP LOCKED`; `DeploymentLeaseTests` requires `AEGISOPS_TEST_POSTGRES` and returns without running when unset. PostgreSQL is unavailable here. |
| P1-17 | An `IAuditWriter` abstraction is used for state changes | Verified | API, AI gateway, and Worker audit writes use the Application-layer contract; scoped `EfAuditWriter` adds to the existing context without saving independently. The focused unit-of-work test and full suite passed. |
| P1-18 | Audit endpoint exposes records | Verified | `/api/v1/audit` is mapped and permission-protected. |
| P1-19 | Audit coverage is tested for all Phase 1 actions | Unverified | Audit writes exist on key paths, but no audit-coverage test was found. |
| P1-20 | Phase 1 frontend views are implemented: login/dashboard, project/artifact views, deployments, approvals, policies/rule editing, audit, and admin | Verified | Components are present in `src/AegisOps.Web/src/App.tsx`; `npm run build` passed. End-to-end seeded-data behavior is tracked separately in P1-24. |
| P1-21 | Policy engine matrix and deployment state-machine tests pass | Verified | Included in the latest full solution run with Redis enabled: 155 passed, 0 failed, 0 skipped. |
| P1-22 | Integration flow tests exercise the full control-plane deployment loop | Unverified | Tests cover state and authorization seams; full request-to-noop-success flow was not run. |
| P1-23 | ADR statuses are updated where decisions are implemented | Verified | `docs/adr/README.md` lists ADRs 0001–0011 as Accepted. |
| P1-24 | Journey A and Journey D complete from the seeded UI, with every step in the audit log | Unverified | Requires a running seeded application and browser verification; Docker/PostgreSQL are unavailable. |

### Phase 1 verification caveats

- The full test suite passed with Redis, but `DeploymentLeaseTests` silently returns when `AEGISOPS_TEST_POSTGRES` is absent. Therefore, the pass count does not prove PostgreSQL locking, migration, or lease behavior ran.
- `EfAuditWriterTests` verifies that the scoped writer adds an event to the shared `AegisOpsDbContext` unit of work without saving independently; audit coverage for every Phase-1 action remains unverified (P1-19).
- PostgreSQL-backed job claiming and end-to-end request-to-execution remain unverified in this environment; audit coverage is tracked separately in P1-19.

## Phase 2 — CI integration & target applications (weight: 1.5 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P2-01 | Service template contains a minimal API | Verified | `aegisops-service-template` local `main` tracks `src/Service/Program.cs` and its project file. |
| P2-02 | Service template contains a Dockerfile and tests | Verified | Both are tracked in the template's local `main` tree. |
| P2-03 | Template CI builds/tests the service and publishes its image | Missing | Template `main` CI references `AegisOps.slnx` and `src/AegisOps.Web`, which are absent from that repo; no image build/push step is configured. |
| P2-04 | Site-service target repo exists with app, Dockerfile, and tests | Verified | `aegisops-site-service` local `main` tracks those files. |
| P2-05 | Circuit-service target repo exists with app, Dockerfile, and tests | Verified | `aegisops-circuit-service` local `main` tracks those files. |
| P2-06 | Notification-service target repo exists with app, Dockerfile, and tests | Verified | `aegisops-notification-service` local `main` tracks those files. |
| P2-07 | All three target repos build/test and publish container images to GHCR | Missing | Their local `main` CI workflows also reference absent AegisOps solution/web paths and contain no Docker build/push steps. Hosted run status could not be checked (`gh` is unavailable). |
| P2-08 | Composite reporting action registers artifact, uploads scans, and requests deployment | Verified | `.github/actions/aegisops-report`; `bash tests/report-action.sh` passed. Test uses mocked HTTP, not a live API. |
| P2-09 | Composite action fails closed for bad/missing staging evidence | Verified | Contract test passed for missing/invalid SARIF, missing test summary, and artifact conflict cases. |
| P2-10 | Self-hosted runner or tunnel provides target CI connectivity | Unverified | No live connectivity/runner evidence was collected. |
| P2-11 | `ci-replay.sh` supports offline replay from saved payloads | Unverified | Script exists and passes `bash -n`; it was not run against a live API. |
| P2-12 | Saved replay fixtures exist for all three target services | Missing | Fixtures exist for `site-service` and `circuit-service`; no notification-service fixture is tracked. |
| P2-13 | Project settings can create/revoke API keys and display `LastUsedAt` | Verified | API-key UI and API endpoints are present; `ApiKeyAuthenticationHandler` updates usage and the frontend displays it; frontend build passed. |
| P2-14 | Redis idempotency store/filter enforces replay ownership and request consistency | Verified | Redis implementation is wired into artifact/deployment endpoints; `IdempotencyTests` with `AEGISOPS_TEST_REDIS=localhost:6379` passed 5/5. |
| P2-15 | Push to `circuit-service/main` creates an artifact and Staging request without manual steps | Unverified | No GitHub Actions run/live AegisOps API verification; local `main` workflow is currently configured for the wrong repository layout. |

### Phase 2 repository-state caveat

The audit inspected local `main` refs, which match their locally cached `origin/main` refs. `gh` is not installed, so remote state may have advanced since the last fetch and hosted Actions/GHCR publication could not be checked. Several sibling service worktrees have user-modified files; they were inspected read-only and not changed.

## Phase 3 — Security scanning (weight: 2 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P3-01 | Scan upload endpoint accepts multipart SARIF reports and stores report files | Unverified | `UploadScan` implements multipart validation, 20 MiB bound, and report storage; no live database-backed upload test was run. |
| P3-02 | Reports up to 2 MiB parse inline; larger reports enqueue an async parse job | Unverified | `ScanReportStore` implements the 2 MiB threshold and `ParseScanReport` queue path; DB-backed job execution was not tested. |
| P3-03 | SARIF parser normalizes Gitleaks, Semgrep, and Trivy findings | Verified | `SarifReportParserTests` covers scanner severity and Trivy package/CVE/fingerprint extraction; focused parser/policy test run passed 21/21. |
| P3-04 | Finding entities, summaries, and stable fingerprints are produced | Verified | Parser tests assert normalized fields/fingerprint; finding and scan summary models are present. |
| P3-05 | Project suppressions match fingerprints and apply to later scans | Unverified | Endpoint/store logic exists, but no executed suppression persistence/re-scan test was found. |
| P3-06 | Findings explorer and dedicated finding-detail UI exist | Missing | Findings are shown contextually within deployment details; no project-wide findings navigation/detail view was found. |
| P3-07 | Findings API lists artifact findings and suppression endpoint exists | Verified | `/api/v1/artifacts/{id}/findings` and project suppression routes are mapped. API runtime behavior remains unverified. |
| P3-08 | `RequireScan` and `MaxFindings` gates are implemented and tested | Verified | `PolicyEngineTests` covers scanner requirements, stale/missing scans, thresholds, unknown findings, and severity behavior; full and focused tests passed. |
| P3-09 | Development seed creates Staging/Production scan gates | Unverified | `DevelopmentUserSeed` defines global critical and Staging scan policies; seeded database state was not exercised. |
| P3-10 | Gitleaks, Semgrep, and Trivy run in template and target-repo CI | Missing | Scanner steps appear in copied workflows, but `main` workflows target absent AegisOps solution/web paths, so the service CI is not valid for those repos. |
| P3-11 | Vulnerable demo branches exist for target services | Unverified | Local branch refs include `demo/vulnerable`; remote branch state and actual workflow execution were not verified. |
| P3-12 | AegisOps CI fails on critical findings | Verified | Root CI config runs Trivy at `CRITICAL` severity with `exit-code: "1"`; hosted run status is unavailable. |
| P3-13 | A push to `demo/vulnerable` is denied with a finding explanation (Journey B) | Unverified | Requires working target CI, API/PostgreSQL, and browser/runtime demonstration; target workflows are currently misconfigured in local `main` refs. |

### Phase 3 verification evidence

- Focused parser and policy tests: 21 passed, 0 failed, 0 skipped.
- The latest complete solution test run passed 155/155 with Redis enabled, but did not exercise the PostgreSQL-backed scan upload/async parse path.

## Phase 4 — Worker & Docker executor (weight: 2 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P4-01 | Docker executor pulls the artifact by digest and supports GHCR credentials | Unverified | `DockerDeploymentExecutor` uses `DockerImageRef.ForPull` and `DOCKER_GHCR_USER`/`DOCKER_GHCR_TOKEN`; no live registry/Docker execution was possible. |
| P4-02 | Executor replaces only owned containers and adds deployment labels | Verified | Ownership-label guards and labels are implemented; focused `DockerTargetOwnershipTests` passed as part of 28 API tests. |
| P4-03 | Health-check failure removes the new container and restores the prior one | Unverified | Rollback/restore path exists in the executor; no Docker daemon was available to exercise it. |
| P4-04 | Environment target editor validates and saves configuration | Verified | UI editor and API/domain validation are implemented; frontend build passed. |
| P4-05 | Environment runtime endpoint reports Docker target status | Unverified | Scoped endpoint and UI button exist; live Docker inspection was unavailable. |
| P4-06 | Target Compose definitions provide the expected target services and ports | Verified | `deploy/docker-compose.targets.yml` defines staging and production containers for all three GHCR image names and their ports. |
| P4-07 | Target Compose file declares target networks | Missing | No `networks` configuration is present in `deploy/docker-compose.targets.yml`. |
| P4-08 | Worker Compose service mounts Docker socket and configures `group_add` | Missing | Root Compose mounts `/var/run/docker.sock`; no `group_add` setting is present. |
| P4-09 | Docker socket proxy option is documented | Verified | `docs/14-devops-and-infrastructure.md` documents the proxy alternative. |
| P4-10 | Rollback API endpoint is available and authorized | Verified | Endpoint is mapped; rollback authorization/state tests are included in the focused API test run (28 passed). |
| P4-11 | Admin can list dead-letter jobs and retry eligible jobs | Verified | Admin jobs endpoints expose list/status and retry; API project compiles and tests pass. |
| P4-12 | Queue-depth metric is emitted | Missing | No queue-depth metric implementation was found under `src/`. |
| P4-13 | Worker exits cleanly on shutdown cancellation | Verified | `DeploymentJobService` catches the stopping-token cancellation and returns. |
| P4-14 | Docker-backed Staging deployment serves the new version and rolls back on forced health failure | Unverified | Requires Docker, published GHCR images, PostgreSQL, and a running deployment; none was exercised. |
| P4-15 | Optional `RunSecurityScan` scanner-container job is implemented | Missing (optional) | No `Worker:RunScans`/`RunSecurityScan` implementation was found; optional, so excluded from mandatory completion scoring unless scope is reaffirmed. |

### Phase 4 verification evidence

- `DockerTargetOwnershipTests` and `DeploymentAuthorizationTests`: 28 passed, 0 failed.
- `DeploymentStateTests`: 9 passed, 0 failed.
- Docker daemon and PostgreSQL were unavailable; these results do not verify container pull/start/health/rollback or DB-backed queue processing.

## Phase 5 — Real-time (weight: 1 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P5-01 | Authenticated SignalR hub accepts active users and rejects non-user actors | Verified | `OpsHub` uses authorization and active-user/access resolution; TestServer coverage passed in the 28-test focused API run. |
| P5-02 | Worker-to-API Redis pub/sub delivers committed deployment update hints | Unverified | `DeploymentUpdates` publishes and API listener subscribes; the TestServer test manually dispatches and does not exercise Redis end to end. |
| P5-03 | Live update delivery rechecks membership/role and avoids cross-project leaks | Verified | `DeploymentAuthorizationTests` covers active-user, membership, roles, project isolation, and eligible approval sessions; focused tests passed. |
| P5-04 | Two browser sessions refresh after SignalR invalidation | Verified | `npm run test:e2e`: 3 passed; two-session test verifies REST refresh after a mocked SignalR frame. |
| P5-05 | Frontend exposes live/connecting/polling status and reconnects | Verified | `deploymentUpdates.ts` + App status UI; Playwright two-session test passed. |
| P5-06 | Approval-needed toast is delivered to eligible approvers within one second | Unverified | Hub sends `ApprovalQueueChanged` and UI creates a toast; no end-to-end latency assertion or real Redis/browser run was performed. |
| P5-07 | Deployment updates work without 10-second polling | Missing | `App.tsx` retains 10-second polling timers as fallback for dashboard/approval/deployment views; roadmap goal is “no polling.” |
| P5-08 | Intermediate Docker timeline steps stream live | Missing | Roadmap states timeline updates are persisted at job completion; no intermediate-step streaming implementation was found. |
| P5-09 | Team webhooks support encrypted settings, event selection, attempt history, and recent-window summaries | Verified | Implemented and documented in `phase5-webhooks.md`; full suite passed, including local TLS/signature/attempt-history coverage. |
| P5-10 | Webhook all-time/telemetry metrics and historical replay are available | Missing | Webhook notes identify these as remaining work. |
| P5-11 | Public external HTTPS receiver path is integration-tested | Unverified | Existing webhook tests use a local TLS receiver; public DNS/IP pinning and production certificate path are not verified. |
| P5-12 | Compose-backed browser test demonstrates real deployment updates and approval toast | Unverified | Playwright coverage mocks SignalR/REST; Docker/PostgreSQL were unavailable. |
| P5-13 | Full Phase 5 done criterion is demonstrated | Unverified | The required live two-session deployment timeline plus sub-second approver toast has not been demonstrated end to end. |

### Phase 5 verification evidence

- `npm run test:e2e`: 3 passed, 0 failed; the SignalR update scenario uses mocked WebSocket frames and REST responses.
- `DeploymentAuthorizationTests`/`DockerTargetOwnershipTests`: 28 passed, 0 failed; includes TestServer SignalR client delivery and access checks, but not live Redis pub/sub.
- Full solution run with local Redis: 155 passed, 0 failed, 0 skipped. PostgreSQL-backed Compose behavior remains unverified.

## Phase 6 — AI gateway (weight: 2 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P6-01 | Global/team `AiModel` and `AiPolicy` persistence and policy controls exist | Verified | Entities, EF configuration, migrations, policy endpoints, and single-enabled-policy enforcement are present; AI gateway tests passed. |
| P6-02 | Ollama model sync and chat client follow the provider contract | Verified | `OllamaLlmClient` implements `/api/tags` and non-streaming `/api/chat`; fake-HTTP contract test passed as part of 43 AI tests. Live Ollama remains separate. |
| P6-03 | Gateway enforces authorization, model allow-list, policy, input bounds, inspection, rate limits, provider call, and audit | Verified | `AiGatewayTests` covers policy, prompt bounds, sensitive-data handling, provider failures/timeouts, response redaction, and audit behavior; 43/43 AI tests passed. |
| P6-04 | Redis Lua limiter enforces minute/day windows atomically | Verified | The 43-test AI run included the Redis integration test with `AEGISOPS_TEST_REDIS=localhost:6379`; all passed. |
| P6-05 | Detector covers Malaysian NRIC/phone, private IP, and other secrets with tests | Verified | Detector test vectors cover NRIC, Malaysian phone, private IPv4, GitHub/AWS tokens, and credit cards; AI test run passed. |
| P6-06 | `/ai/chat`, `/ai/models`, `/ai/requests`, and policy/model-management endpoints are mapped and authorization-protected | Verified | `Program.cs` route mappings and permission requirements are present; AI management route tests passed. |
| P6-07 | Deployment summary and finding explanation use the governed gateway and audit purposes | Verified | Endpoint tests exercise both features, access control, purpose logging, and pending-approval restriction; included in 43 passing AI tests. |
| P6-08 | Frontend request history, policy editor, deployment summary, and finding explanation exist | Verified | `App.tsx` contains governance/history/policy UI and summary/explanation actions; Playwright E2E passed 3/3. |
| P6-09 | Frontend chat playground with model selector and GovernancePanel exists | Missing | No frontend call to `/api/v1/ai/chat` or model selector/chat page was found; current AI UI is governance/history plus feature-specific summaries/explanations. |
| P6-10 | Compose provides an `ollama` service and `ollama-init` model-pull profile | Missing | Only `docker-compose.yml` and target compose are tracked; neither defines Ollama or `ollama-init`. |
| P6-11 | README includes local-model hardware guidance | Missing | README mentions Ollama but has no RAM/GPU/model hardware notes; detailed notes exist in AI docs instead. |
| P6-12 | Journey C runs against a real local Ollama model | Unverified | Ollama is not listening on `127.0.0.1:11434`; Docker is unavailable. Current tests use fake `ILlmClient`/HTTP handlers. |
| P6-13 | AI request log demonstrates blocked/redacted entries and production approver summary | Unverified | API/UI features and mocked tests exist; no seeded PostgreSQL + live Ollama approval journey was run. |

### Phase 6 verification evidence

- Focused `AiGatewayTests` with local Redis: 43 passed, 0 failed, 0 skipped.
- Playwright E2E: 3 passed, 0 failed; AI endpoints are mocked in browser tests.
- No Ollama Compose service/profile is tracked and local port 11434 is closed.

## Phase 7 — Observability (weight: 1.5 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P7-01 | Custom `Meter("AegisOps")` metrics cover deployment, scan, AI, jobs, and audit activity | Missing | No custom `Meter`, counter, histogram, or observable instrument was found under `src/`. |
| P7-02 | API and Worker expose Prometheus `/metrics` endpoints | Missing | No `/metrics` route or Prometheus exporter is implemented. |
| P7-03 | `ActivitySource` traces use cases, policy, scans, Ollama, and Docker execution | Missing | No `ActivitySource`, `ActivityLink`, or OpenTelemetry setup was found in `src/`. |
| P7-04 | Prometheus profile, scrape config, Grafana dashboards, and alert rules are provisioned | Missing | No tracked Prometheus/Grafana provisioning or dashboard/rule files exist. |
| P7-05 | Readiness reports PostgreSQL/Redis/migration health and degraded Ollama/Docker states | Missing | `/health/live` and `/health/ready` are mapped, but API/Worker register only bare `AddHealthChecks()` with no service checks. |
| P7-06 | UI displays degraded Ollama/Docker readiness | Missing | No health-status integration or Ollama/Docker readiness banner was found; generic API error banners are not a health indicator. |
| P7-07 | Serilog structured JSON logging, enrichers, request logging, and PII policy are configured | Missing | Application code uses `ILogger`; no Serilog package/configuration/request middleware or structured logging policy was found in the implementation. |
| P7-08 | Three provisioned dashboards show live data during a demo | Unverified | No dashboards are provisioned and runtime services are unavailable. |

### Phase 7 verification caveat

`docs/15-observability.md` describes a detailed target design, but the corresponding runtime instrumentation/provisioning was not found in tracked source. Documentation alone does not earn implementation credit under the roadmap’s measurement rule.

## Phase 8 — Hardening, documentation, demo (weight: 1.5 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P8-01 | Security hardening checklist is verified by successful scans/review | Unverified | CI defines Gitleaks, Semgrep, and Trivy, but no successful hosted scan result or complete checklist sign-off was collected. |
| P8-02 | Complete authorization matrix test suite covers every permission × role | Missing | Targeted deployment authorization tests cover important paths, but no exhaustive permission-by-role matrix test exists. |
| P8-03 | Rate limits are tuned and demonstrated under load | Unverified | Auth has a 10/minute IP fixed window; AI has Redis minute/day windows and passing tests. No load/tuning evidence exists. |
| P8-04 | SPA security headers include the planned CSP | Missing | No CSP or equivalent response-header middleware/configuration was found. |
| P8-05 | Audit coverage test verifies all security-sensitive actions | Missing | Audit writes exist on some paths, but no audit-coverage test exists; this was also a Phase 1 acceptance check. |
| P8-06 | AI content retention job deletes stored prompts/responses after configured retention | Missing | Settings/docs describe content retention, but no retention worker/job implementation was found. |
| P8-07 | Scan report cleanup removes expired reports | Missing | Report storage exists; no scheduled cleanup/purge implementation was found. |
| P8-08 | Job cleanup removes succeeded jobs after seven days and handles dead letters | Missing | Database design documents this behavior, but no cleanup implementation was found. |
| P8-09 | `reset-demo.sh` resets seeded/demo state | Missing | No tracked reset script exists. |
| P8-10 | `docs/demo-script.md` walks through the four v1 journeys | Missing | No tracked demo script exists; only Compose setup and a deployment smoke script are present. |
| P8-11 | README includes a 60-second GIF and screenshots | Missing | No tracked GIF/screenshots were found. |
| P8-12 | README provides final onboarding/run instructions | Verified | README contains Compose startup, seeded admin guidance, and deployment smoke-script instructions. This does not prove the full four-journey demo. |
| P8-13 | ADR statuses reflect accepted decisions | Verified | ADR index and ADR files show the recorded decisions as Accepted. |
| P8-14 | `CHANGELOG.md` and Interview notes are present | Missing | Neither deliverable is tracked. |
| P8-15 | v1.0.0 release tag and GHCR images are published | Unverified | No local or remote `v1.0.0` tag and no image-publishing workflow were found; GHCR registry contents were not checked. |
| P8-16 | Fresh clone can run Compose and reproduce all four journeys in under 15 minutes | Unverified | Docker is unavailable, no demo script exists, and database-backed end-to-end journeys were not exercised. |

### Phase 8 verification caveat

The security scanning workflow and README setup path exist, but the release/demo acceptance criterion is not demonstrated. Security headers, audit-coverage tests, retention/cleanup, and release assets are concrete gaps rather than runtime-only unknowns.

## Overall weighted estimate

All Phase 0–8 checks have now been enumerated. The score counts only `Verified` checks, treats `Unverified`, `Missing`, and unresolved `Scope mismatch` checks as zero, excludes the explicitly optional P4-15 check, and applies the roadmap's phase-duration weights. This is a verified-evidence floor, not a claim that every unverified feature is absent.

| Phase | Weight (weeks) | Verified / eligible checks | Weighted contribution |
| --- | ---: | ---: | ---: |
| 0 | 1 | 19 / 28 | 4.38 percentage points |
| 1 | 3 | 9 / 24 | 7.26 percentage points |
| 2 | 1.5 | 9 / 15 | 5.81 percentage points |
| 3 | 2 | 5 / 13 | 4.96 percentage points |
| 4 | 2 | 7 / 14 | 6.45 percentage points |
| 5 | 1 | 5 / 13 | 2.48 percentage points |
| 6 | 2 | 8 / 13 | 7.94 percentage points |
| 7 | 1.5 | 0 / 8 | 0.00 percentage points |
| 8 | 1.5 | 2 / 16 | 1.21 percentage points |
| **Total** | **15.5** | **64 / 144** | **40.49% complete** |

**Estimated remaining: 59.51%** by the same weighted, evidence-based measure. Status totals are 64 Verified, 44 Unverified, 36 Missing, and 0 Scope mismatch. P0-09 and the deployment architecture criterion are rebaselined to match the agreed solution; `IAuditWriter` is now implemented and verified. The durable Worker flow remains unverified until PostgreSQL-backed execution can run.

### Highest-impact next steps

1. Run Compose with PostgreSQL and verify the durable Worker flow plus Journeys A and D end to end.
2. Implement Phase 7 observability and service-specific readiness; it currently has 0/8 verified checks.
3. Close Phase 8 gaps: CSP/audit coverage, retention/cleanup, AI demo assets, and a repeatable release path.

The roadmap/audit rebaseline and IAuditWriter implementation are not committed. Review the documentation and test output together before committing this combined change.
