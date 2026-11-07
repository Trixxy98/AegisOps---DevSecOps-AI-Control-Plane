# v1 progress audit

Audit baseline: 2026-11-04, starting commit `a1f2ef7` on `dev`; this refreshed estimate includes local, uncommitted test and audit-evidence updates.

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
| P0-18 | Compose services build/start and API readiness is healthy | Verified | `docker compose up --build -d` rebuilt and started all five services; PostgreSQL/Redis are healthy and `/health/ready` returned HTTP 200. |
| P0-19 | CI workflow configures backend restore/build/test and report-contract checks | Verified | Steps are present in `.github/workflows/ci.yml`; hosted run status is tracked separately. |
| P0-20 | CI workflow configures frontend build and browser E2E | Verified | Steps are present in `.github/workflows/ci.yml`; hosted run status is tracked separately. |
| P0-21 | CI workflow configures Gitleaks, Semgrep, and Trivy | Verified | Security job steps are present in `.github/workflows/ci.yml`. |
| P0-22 | `scripts/dev-up.sh` exists | Missing | Not tracked. |
| P0-23 | `gen-secrets.sh` exists | Missing | Not tracked at repository root or under `scripts/`. |
| P0-24 | `.env.example` exists | Verified | Tracked. |
| P0-25 | Dependabot configuration exists | Missing | No tracked `.github/dependabot.yml` or `.yaml` found. |
| P0-26 | Compose serves UI at `:3000` | Verified | `http://127.0.0.1:3000` returned HTTP 200 and rendered the AegisOps sign-in page. |
| P0-27 | API `/health/ready` is healthy in Compose | Verified | `http://127.0.0.1:8080/health/ready` returned HTTP 200 before Phase 7; after adding dependency probes it returned HTTP 200 Degraded with PostgreSQL/Redis Healthy and Ollama Degraded (see P7-05). |
| P0-28 | CI is green on `main` | Unverified | No successful main-branch CI run evidence collected. |

### Verification evidence

- Full solution test run with local Redis and a disposable PostgreSQL cluster: 156 passed, 0 failed, 0 skipped.
- `npm run build` in `src/AegisOps.Web`: passed.
- At the original Compose baseline, PostgreSQL and Redis were healthy, API `/health/live` and `/health/ready` returned HTTP 200, and the web app returned HTTP 200. No saved local login was available then; a vault login was subsequently used to verify the authenticated Phase 7 UI banner.

### Phase 0 remaining gaps

The roadmap has been rebaselined to the three test projects already in `AegisOps.slnx`. It still calls for `scripts/dev-up.sh`, `gen-secrets.sh`, Dependabot, ESLint, and Prettier, which are not tracked/configured.

## Phase 1 — Core control plane (weight: 3 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P1-01 | ASP.NET Identity and JWT authentication are registered | Verified | `Program.cs` and `JwtAuthenticationExtensions.cs`; solution test/build evidence passes. |
| P1-02 | Login endpoint issues an access token | Verified | `IdentityFlowTests` drives the real `Login` handler and JWT bearer validation over TestServer/SQLite: valid credentials return a usable access token, a hashed refresh token, an HttpOnly/Secure/SameSite=Strict cookie, and an `auth.login` audit event; wrong password, unknown user, inactive user, and blank input are rejected without tokens; forged/expired JWTs return 401. Full suite passed. |
| P1-03 | Refresh tokens rotate and can be revoked | Verified | `IdentityFlowTests` verifies rotation (old token revoked and linked to successor in the same family), reuse detection revoking the whole family, rejection of missing/unknown/expired tokens and deactivated users, logout revoking the family with an `auth.logout` audit event, and refusal to revoke another user's token. Full suite passed. |
| P1-04 | Roles and development demo users are seeded | Unverified | Seed implementation exists; seeded runtime state was not exercised against PostgreSQL. |
| P1-05 | Permission matrix is tested across roles and actions | Unverified | Deployment authorization tests cover important access paths, but no complete permission × role matrix test was found. |
| P1-06 | API-key domain model and authentication handler exist | Verified | `IdentityFlowTests` authenticates a real `aok_` key through the Smart scheme → `ApiKeyAuthenticationHandler`, asserting the `apiKey:{id}` principal and `LastUsedAt`; wrong secret, malformed, unknown-prefix, revoked, and expired keys return 401 without marking use. Scope claims are not asserted because the SQLite test model ignores the PostgreSQL scopes array. Full suite passed. |
| P1-07 | Team/member APIs and membership scoping work | Unverified | Routes and implementation exist; deployment authorization tests cover some membership scoping, not the full organization API flow. |
| P1-08 | Project creation provisions Dev/Staging/Production environments | Verified | `ProjectAndPolicyEndpointTests` creates a project through the real handler with JWT + `projects:write` authorization: 201 with normalized slug, exactly three persisted noop environments ordered Development/Staging/Production (0/1/2), a `project.created` audit event, and identical read-back via `GetProject`. Duplicate slug (409), unknown team (404), invalid input (400), Developer (403), and anonymous (401) create nothing. Full suite passed. |
| P1-09 | Repository configuration and noop environment targets work | Verified | `ProjectAndPolicyEndpointTests` creates then replaces the GitHub repository in place (same ID, two `repository.upserted` audits, visible via `GetProject`), rejects non-GitHub providers and unknown projects, sets the Production target to noop with API-key production access, and rejects unknown target types, empty patches, cross-project environment IDs (404), and Developer access (403). Docker targets remain covered by Phase 4 checks. Full suite passed. |
| P1-10 | Artifact registration via UI/API, lists/details, and test summary work | Unverified | API/UI implementation exists and `ArtifactReplayTests` cover identity matching; no end-to-end artifact registration flow evidence found. |
| P1-11 | Policy entities and the six Phase 1 rule behaviors are implemented and tested | Verified | `PolicyEngineTests` covers tests, approvals, branch, prior environment, digest, and deployment window; full suite passed. |
| P1-12 | Rule-type catalogue and policy simulation endpoints work | Verified | `ProjectAndPolicyEndpointTests` checks the catalogue lists exactly the `PolicyRuleType` values with valid effects and object parameters (401 anonymous). Simulation over HTTP returns Deny (failing tests/branch), RequireApproval (project-scoped two-approval rule), and Allow (tier/scope filtering excludes Production-only and other-project policies), returns 404 for unknown or cross-project inputs, and persists no deployments or audit events. The SQLite fixture persists policy tiers rather than ignoring them. Full suite passed. |
| P1-13 | Deployment state transitions, approval quorum, and cancellation are tested | Verified | `DeploymentStateTests` exercises transitions; `DeploymentAuthorizationTests` covers approval/cancellation authorization; full suite passed. |
| P1-14 | Request → durable PostgreSQL job → separate Worker `DeploymentJobService`/`DeploymentProcessor` → evaluate → noop execute → succeeded | Verified | With Compose running, an authenticated UI request for `circuit-service` artifact `v0.0.0-main.52181e3` to Development completed Succeeded. The timeline showed Allow, `Noop executor started`, and simulated success; the audit log recorded `deployment.requested`, `deployment.evaluated`, and `deployment.succeeded`. PostgreSQL lease/recovery tests also passed. |
| P1-15 | Separation of duties is enforced for approvals | Verified | `DeploymentAuthorizationTests` covers requester/non-requester and role restrictions; full suite passed. |
| P1-16 | PostgreSQL jobs use `SKIP LOCKED`, retry/backoff, and stale-job recovery | Verified | `DeploymentLeaseTests` ran against a disposable local PostgreSQL 16 cluster and verified parallel claims, lease renewal, and stale recovery. `DeploymentStateTests` verifies retries/dead-lettering and now asserts exponential backoff; focused suite passed 10/10. |
| P1-17 | An `IAuditWriter` abstraction is used for state changes | Verified | API, AI gateway, and Worker audit writes use the Application-layer contract; scoped `EfAuditWriter` adds to the existing context without saving independently. The focused unit-of-work test and full suite passed. |
| P1-18 | Audit endpoint exposes records | Verified | `/api/v1/audit` is mapped and permission-protected. |
| P1-19 | Audit coverage is tested for all Phase 1 actions | Unverified | Audit writes exist on key paths, but no audit-coverage test was found. |
| P1-20 | Phase 1 frontend views are implemented: login/dashboard, project/artifact views, deployments, approvals, policies/rule editing, audit, and admin | Verified | Components are present in `src/AegisOps.Web/src/App.tsx`; policy rules now expose JSON parameter editing with explicit save, preserving policy metadata. `npm run build` and all 4 Playwright tests passed. |
| P1-21 | Policy engine matrix and deployment state-machine tests pass | Verified | Included in the latest full solution run with Redis and PostgreSQL enabled: 156 passed, 0 failed, 0 skipped. |
| P1-22 | Integration flow tests exercise the full control-plane deployment loop | Unverified | A live Compose UI request completed through the Worker/noop path, but no automated integration test for the full API/Worker loop was run. |
| P1-23 | ADR statuses are updated where decisions are implemented | Verified | `docs/adr/README.md` lists ADRs 0001–0011 as Accepted. |
| P1-24 | Journey A and Journey D complete from the seeded UI, with every step in the audit log | Unverified | Journey D's Production baseline was updated in the UI from v2 to v4 with the exact `Asia/Kuala_Lumpur` Fri 18:00–Mon 08:00 blackout schedule; two `policy.updated` successes were read back in the audit log, and the policy-engine boundary test passed. A live Friday-night deployment denial was not exercised. Journey A remains incomplete: seeded `circuit-service` shows `v0.0.0-demo-vulnerable` and `v0.0.0-main.52181e3`, not `v1.8.2`; an older 2/2-approved production deployment exists, but the matching staging→production chain was not verified. |

### Phase 1 verification caveats

- `DeploymentLeaseTests` silently returns when `AEGISOPS_TEST_POSTGRES` is absent; for the latest full run it was set to a disposable local PostgreSQL 16 cluster, so migrations, parallel claims, lease renewal, and stale recovery executed.
- `EfAuditWriterTests` verifies that the scoped writer adds an event to the shared `AegisOpsDbContext` unit of work without saving independently; audit coverage for every Phase-1 action remains unverified (P1-19).
- Compose request-to-noop execution is verified in the authenticated UI. Journey D's policy update, version increments, audit entries, and window boundaries are verified; the live denial request and full Journey A staging→production sequence remain unverified.

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
- The latest complete solution test run passed 156/156 with Redis and PostgreSQL enabled, but did not exercise the PostgreSQL-backed scan upload/async parse path.

## Phase 4 — Worker & Docker executor (weight: 2 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P4-01 | Docker executor pulls the artifact by digest and supports GHCR credentials | Unverified | `DockerDeploymentExecutor` uses `DockerImageRef.ForPull` and `DOCKER_GHCR_USER`/`DOCKER_GHCR_TOKEN`; Compose is running, but no authenticated GHCR pull/deployment was tested. |
| P4-02 | Executor replaces only owned containers and adds deployment labels | Verified | Ownership-label guards and labels are implemented; focused `DockerTargetOwnershipTests` passed as part of 28 API tests. |
| P4-03 | Health-check failure removes the new container and restores the prior one | Verified | Staging deployment `01a17b5b-3049-73e5-b9ce-782c31d1a1ab` recorded `HealthCheckFailed` → `RolledBack` → `Failed`; the healthy artifact remained current and its service was live and healthy on November 5, 2026. The event schema does not retain container IDs, so historical ID equality is not independently provable. See `docs/staging-rollback-validation.md`. |
| P4-04 | Environment target editor validates and saves configuration | Verified | UI editor and API/domain validation are implemented; frontend build passed. |
| P4-05 | Environment runtime endpoint reports Docker target status | Missing | Authenticated Staging runtime check returned `inspectionAvailable:false`, `containers:null`, and “Docker runtime inspection is unavailable to the API.” |
| P4-06 | Target Compose definitions provide the expected target services and ports | Verified | `deploy/docker-compose.targets.yml` defines staging and production containers for all three GHCR image names and their ports. |
| P4-07 | Target Compose file declares target networks | Missing | No `networks` configuration is present in `deploy/docker-compose.targets.yml`. |
| P4-08 | Worker Compose service mounts Docker socket and configures `group_add` | Missing | Root Compose mounts `/var/run/docker.sock`; no `group_add` setting is present. |
| P4-09 | Docker socket proxy option is documented | Verified | `docs/14-devops-and-infrastructure.md` documents the proxy alternative. |
| P4-10 | Rollback API endpoint is available and authorized | Verified | Endpoint is mapped; rollback authorization/state tests are included in the focused API test run (28 passed). |
| P4-11 | Admin can list dead-letter jobs and retry eligible jobs | Verified | Admin jobs endpoints expose list/status and retry; API project compiles and tests pass. |
| P4-12 | Queue-depth metric is emitted | Missing | No queue-depth metric implementation was found under `src/`. |
| P4-13 | Worker exits cleanly on shutdown cancellation | Verified | `DeploymentJobService` catches the stopping-token cancellation and returns. |
| P4-14 | Docker-backed Staging deployment serves the new version and rolls back on forced health failure | Verified | Published healthy image run `40439154412` deployed successfully and returned healthy `/health`; distinct forced-failure image run `40442951347` passed CI/scans, received `Allow`, then recorded the 60-second health failure and automatic rollback. Production stayed on its existing noop target/artifact. See `docs/staging-rollback-validation.md`. |
| P4-15 | Optional `RunSecurityScan` scanner-container job is implemented | Missing (optional) | No `Worker:RunScans`/`RunSecurityScan` implementation was found; optional, so excluded from mandatory completion scoring unless scope is reaffirmed. |

### Phase 4 verification evidence

- `DockerTargetOwnershipTests` and `DeploymentAuthorizationTests`: 28 passed, 0 failed.
- `DeploymentStateTests`: 10 passed, 0 failed.
- `python3 tests/staging-docker-contract.py` and `python3 tests/report-storage-contract.py` both passed, checking worker-only Docker socket access and shared report-volume wiring.
- Compose is running and the Development noop queue path passed. A published-image Staging deploy and forced health-failure rollback were verified against the Docker target. The Staging runtime endpoint still cannot inspect Docker from the API, and GHCR credential-authenticated pulling remains unverified.

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
| P5-12 | Compose-backed browser test demonstrates real deployment updates and approval toast | Unverified | Docker/PostgreSQL are now running, but no two-browser Compose test with real SignalR and approval-toast timing was run; existing Playwright coverage mocks SignalR/REST. |
| P5-13 | Full Phase 5 done criterion is demonstrated | Unverified | The required live two-session deployment timeline plus sub-second approver toast has not been demonstrated end to end. |

### Phase 5 verification evidence

- `npm run test:e2e`: 3 passed, 0 failed; the SignalR update scenario uses mocked WebSocket frames and REST responses.
- `DeploymentAuthorizationTests`/`DockerTargetOwnershipTests`: 28 passed, 0 failed; includes TestServer SignalR client delivery and access checks, but not live Redis pub/sub.
- Full solution run with local Redis and disposable PostgreSQL: 156 passed, 0 failed, 0 skipped. Docker-backed Compose behavior remains unverified.

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
| P6-12 | Journey C runs against a real local Ollama model | Unverified | Ollama is not listening on `127.0.0.1:11434`; current tests use fake `ILlmClient`/HTTP handlers. |
| P6-13 | AI request log demonstrates blocked/redacted entries and production approver summary | Unverified | API/UI features and mocked tests exist; no seeded PostgreSQL + live Ollama approval journey was run. |

### Phase 6 verification evidence

- Focused `AiGatewayTests` with local Redis: 43 passed, 0 failed, 0 skipped.
- Playwright E2E: 3 passed, 0 failed; AI endpoints are mocked in browser tests.
- No Ollama Compose service/profile is tracked and local port 11434 is closed.

## Phase 7 — Observability (weight: 1.5 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P7-01 | Custom `Meter("AegisOps")` metrics cover deployment, scan, AI, jobs, and audit activity | Missing | Partial: Worker job counters, attempt duration, lease-expiry and queue-depth gauge exist under `src/AegisOps.Infrastructure/Observability/`; deployment, scan, AI, and audit metrics remain missing. |
| P7-02 | API and Worker expose Prometheus `/metrics` endpoints | Verified | API exports ASP.NET Core request-duration metrics at internal port `9464` only: live Kestrel smoke returned 200 there, while `/metrics` on the API's `:8080` and other routes on `:9464` returned 404. TestServer verifies histogram route labels and port gating. Worker `/metrics` previously exported live job metrics in the PostgreSQL smoke run. Compose config publishes neither metrics port to the host; Compose runtime has not been exercised for this increment (Docker daemon unavailable). |
| P7-03 | `ActivitySource` traces use cases, policy, scans, Ollama, and Docker execution | Missing | No `ActivitySource`, `ActivityLink`, or OpenTelemetry setup was found in `src/`. |
| P7-04 | Prometheus profile, scrape config, Grafana dashboards, and alert rules are provisioned | Unverified | The opt-in Compose overlay, scrape config, five Prometheus rules with passing promtool rule tests, Grafana datasource and one Overview dashboard are tracked. Compose config and offline dashboard contract pass; a locally run Prometheus binary scraped a live API metric and loaded all five rules. Docker daemon was unavailable, so live Compose startup and Grafana dashboard rendering remain unverified. |
| P7-05 | Readiness reports PostgreSQL/Redis/migration health and degraded Ollama/Docker states | Verified | Rebuilt API/Worker images and started PostgreSQL/Redis/API/Worker in Compose. API `/health/live` returned 200 and `/health/ready` returned 200 Degraded: PostgreSQL/Redis Healthy, Ollama Degraded. Worker `/health/live` returned 200 and `/health/ready` returned 200 Healthy: PostgreSQL/Redis/Docker/worker poll all Healthy. Local failure runs returned 503 readiness with 200 liveness; Ollama timeout remained Degraded. Six focused tests and the full 163-test .NET suite passed. |
| P7-06 | UI displays degraded Ollama/Docker readiness | Verified | The signed-in UI polls authenticated `/api/v1/system/dependencies` every 30 seconds/on focus and shows Ollama/Docker/Worker warnings. A real saved-vault login in Compose displayed the Ollama degraded banner with PostgreSQL/Redis/Worker healthy; the API endpoint rejected an unauthenticated request with 401. Two Playwright tests passed for Ollama/Docker degraded and Worker unhealthy responses (Docker degradation was mocked, not induced on the live daemon), and the API handler test passed. |
| P7-07 | Serilog structured JSON logging, enrichers, request logging, and PII policy are configured | Missing | Application code uses `ILogger`; no Serilog package/configuration/request middleware or structured logging policy was found in the implementation. |
| P7-08 | Three provisioned dashboards show live data during a demo | Missing | One Overview dashboard using currently exported API and Worker metrics is provisioned, but Delivery/AI Governance dashboards do not exist and no live Grafana demo has run. |

### Phase 7 verification caveat

`docs/15-observability.md` separates implemented metrics from the target design. Readiness and the signed-in degraded-status UI are verified in Compose/browser tests; Worker job metrics are partly implemented and API/Worker `/metrics` endpoints have passed live local smoke tests. An opt-in Prometheus/Grafana Compose overlay, alert rules, and one Overview dashboard now exist and pass static/promtool checks; a locally run Prometheus scraped the API metrics successfully. Compose startup and Grafana rendering remain unverified. The remaining custom metrics, tracing, two additional dashboards, and structured logging are not complete. Documentation alone does not earn implementation credit under the roadmap’s measurement rule.

## Phase 8 — Hardening, documentation, demo (weight: 1.5 / 15.5 weeks)

| ID | Acceptance check | Status | Evidence / gap |
| --- | --- | --- | --- |
| P8-01 | Security hardening checklist is verified by successful scans/review | Unverified | CI defines Gitleaks, Semgrep, and Trivy, but no successful hosted scan result or complete checklist sign-off was collected. |
| P8-02 | Complete authorization matrix test suite covers every permission × role | Missing | Targeted deployment authorization tests cover important paths, but no exhaustive permission-by-role matrix test exists. |
| P8-03 | Rate limits are tuned and demonstrated under load | Unverified | Auth has a 10/minute IP fixed window; AI has Redis minute/day windows and passing tests. No load/tuning evidence exists. |
| P8-04 | SPA security headers include the planned CSP | Verified | nginx sends a CSP with same-origin scripts/styles, same-host SignalR WebSocket connections, and no inline/eval scripts; it also sends `nosniff`, `DENY`, and `no-referrer`. The live Compose smoke test passed against port 3000, `nginx -t` passed, a saved-vault sign-in and project navigation worked without observed CSP violations, and all 6 Playwright tests passed. |
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
| P8-16 | Fresh clone can run Compose and reproduce all four journeys in under 15 minutes | Unverified | Compose runs in the current checkout, but a fresh-clone timing run and all four database-backed journeys were not exercised; no dedicated all-journeys demo script was found. |

### Phase 8 verification caveat

The security scanning workflow and README setup path exist, and the nginx SPA security headers are verified against the running Compose service. The release/demo acceptance criterion remains undemonstrated; audit-coverage tests, retention/cleanup, and release assets are concrete gaps rather than runtime-only unknowns.

## Overall weighted estimate

All Phase 0–8 checks have now been enumerated. The score counts only `Verified` checks, treats `Unverified`, `Missing`, and unresolved `Scope mismatch` checks as zero, excludes the explicitly optional P4-15 check, and applies the roadmap's phase-duration weights. This is a verified-evidence floor, not a claim that every unverified feature is absent.

| Phase | Weight (weeks) | Verified / eligible checks | Weighted contribution |
| --- | ---: | ---: | ---: |
| 0 | 1 | 22 / 28 | 5.07 percentage points |
| 1 | 3 | 17 / 24 | 13.71 percentage points |
| 2 | 1.5 | 9 / 15 | 5.81 percentage points |
| 3 | 2 | 5 / 13 | 4.96 percentage points |
| 4 | 2 | 9 / 14 | 8.29 percentage points |
| 5 | 1 | 5 / 13 | 2.48 percentage points |
| 6 | 2 | 8 / 13 | 7.94 percentage points |
| 7 | 1.5 | 3 / 8 | 3.63 percentage points |
| 8 | 1.5 | 3 / 16 | 1.81 percentage points |
| **Total** | **15.5** | **81 / 144** | **53.71% complete** |

**Estimated remaining: 46.29%** by the same weighted, evidence-based measure. Status totals are 81 Verified, 30 Unverified, 33 Missing, and 0 Scope mismatch. The Compose request → PostgreSQL-backed Worker → Development noop path, Docker-backed Staging rollback, API/Worker readiness with signed-in UI warnings, and SPA security headers are verified; complete seeded Journeys A/D remain unverified.

### Highest-impact next steps

1. Complete Journey A once the expected `v1.8.2` artifact and CI scan evidence arrive (do not deploy an older artifact to Staging); verify Journey D's live denial during the Friday 18:00–Monday 08:00 MYT blackout and capture the audit trail.
2. Implement the remaining Phase 7 observability: metrics, tracing, dashboards, and structured logging; readiness, the UI degraded-status banner, and API/Worker `/metrics` are verified (3/8 checks).
3. Close remaining Phase 8 gaps: audit coverage, retention/cleanup, AI demo assets, and a repeatable release path.

The Staging rollback, Phase 7 readiness, signed-in degraded-status UI/API, and SPA security-header increments are committed. Live CSP smoke, nginx syntax, authenticated browser navigation, and 6 Playwright tests were verified for the security-header increment; use current Git status before relying on historical worktree notes.

2026-11-06 update: P1-02, P1-03, and P1-06 moved to Verified via `tests/AegisOps.Api.Tests/IdentityFlowTests.cs` (12 tests). Full solution run: 175 passed, 0 failed, 1 skipped. Overall estimate 47.66% → 50.08% (+2.42 percentage points).

2026-11-06 update 2: P1-08, P1-09, and P1-12 moved to Verified via `tests/AegisOps.Api.Tests/ProjectAndPolicyEndpointTests.cs` (6 tests). Full solution run: 181 passed, 0 failed, 1 skipped. Overall estimate 50.08% → 52.50% (+2.42 percentage points).
