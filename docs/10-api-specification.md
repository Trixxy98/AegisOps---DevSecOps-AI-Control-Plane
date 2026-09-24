# 10 — API Specification

Base URL: `/api/v1`. The OpenAPI document is generated at `/openapi/v1.json` and browsable
via Scalar at `/scalar`. This document defines conventions and the endpoint inventory; the
generated OpenAPI is the field-level contract.

## 1. Conventions

| Topic | Rule |
| --- | --- |
| Format | JSON, `camelCase`, UTC ISO-8601 timestamps with `Z`, enums as strings |
| IDs | GUID v7 strings |
| Auth | `Authorization: Bearer <jwt>` or `Bearer aok_…` (API key). See [09](09-identity-and-authorization.md) |
| Errors | RFC 9457 `application/problem+json`: `type`, `title`, `status`, `detail`, `instance`, `traceId`, `errors` (validation dictionary) |
| Validation | `400` with `errors: { "field": ["message"] }` |
| Not found vs forbidden | `404` when the resource is outside the caller's visibility (no existence leak) |
| Pagination | `?page=1&pageSize=25` (max 100) → `{ items, page, pageSize, totalCount }` |
| Sorting / filtering | `?sort=-requestedAt` (prefix `-` = desc); filters as query params listed per endpoint |
| Idempotency | `Idempotency-Key` header on `POST /artifacts`, `POST /deployments` (required for API keys) |
| Correlation | `X-Correlation-Id` echoed back; generated if absent |
| Versioning | URL segment `v1`; breaking changes → `v2` side-by-side |
| Rate limits | Global 300 req/min per principal; auth endpoints 10/min per IP; AI endpoints per policy. `429` + `Retry-After` |
| Long operations | `202 Accepted` with `Location` of the resource to poll (deployments) |
| Timestamps in filters | `?from=…&to=…` inclusive-exclusive |

### Problem types

| `type` suffix | Status | When |
| --- | --- | --- |
| `validation` | 400 | FluentValidation failure |
| `unauthorized` | 401 | missing/invalid token |
| `forbidden` | 403 | authorization denied |
| `not-found` | 404 | |
| `conflict` | 409 | version conflict, duplicate artifact version, illegal state transition |
| `invalid-transition` | 409 | deployment state machine violation (`extensions.from`, `extensions.to`) |
| `ai-blocked` | 403/422/429 | AI gateway block (`extensions.reason`) |
| `rate-limited` | 429 | |

## 2. Endpoint inventory

Legend for **Auth**: permission name, or `key:<scope>` for API keys, `Admin` for role.

### 2.1 Auth

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| POST | `/auth/login` | Login → access token + refresh cookie | anonymous |
| POST | `/auth/refresh` | Rotate refresh token | cookie |
| POST | `/auth/logout` | Revoke refresh family | authenticated |
| GET | `/auth/me` | Profile, roles, teams, permissions | authenticated |
| POST | `/auth/change-password` | | authenticated |

### 2.2 Users & roles (admin)

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| GET | `/admin/users` | List users (`?search=`) | `users:manage` |
| POST | `/admin/users` | Create user with temp password | `users:manage` |
| PATCH | `/admin/users/{id}` | Activate/deactivate, display name | `users:manage` |
| PUT | `/admin/users/{id}/roles` | Set roles | `roles:manage` |

### 2.3 Teams

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| GET | `/teams` | Teams visible to caller | `teams:read` |
| POST | `/teams` | Create team | Admin |
| GET | `/teams/{slug}` | Team detail incl. members, projects | `teams:read` |
| PATCH | `/teams/{slug}` | Rename/describe | `teams:write` |
| PUT | `/teams/{slug}/members/{userId}` | Add/update member role | `teams:write` |
| DELETE | `/teams/{slug}/members/{userId}` | Remove member | `teams:write` |

### 2.4 Projects, environments, repositories

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| GET | `/projects` | Visible projects (`?teamId=&search=&archived=`) | `projects:read` |
| POST | `/projects` | Create project (creates default envs Dev/Staging/Prod) | `projects:write` |
| GET | `/projects/{slug}` | Detail: environments with current artifact, repositories, latest deployments | `projects:read` |
| PATCH | `/projects/{slug}` | Update / archive | `projects:write` |
| GET | `/projects/{slug}/environments` | | `projects:read` |
| POST | `/projects/{slug}/environments` | Add environment | `environments:write` |
| PATCH | `/projects/{slug}/environments/{envId}` | Name, order, `target` JSON | `environments:write` |
| GET | `/projects/{slug}/environments/{envId}/runtime` | Live container status from Docker (Phase 4) | `projects:read` |
| PUT | `/projects/{slug}/repository` | Set repository info | `projects:write` |
| GET | `/projects/{slug}/api-keys` | List keys (prefix, scopes, expiry, last used) | `apikeys:manage` |
| POST | `/projects/{slug}/api-keys` | Create key → returns plain key once | `apikeys:manage` |
| DELETE | `/projects/{slug}/api-keys/{keyId}` | Revoke | `apikeys:manage` |

### 2.5 Artifacts, scans, findings

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| POST | `/projects/{slug}/artifacts` | Register artifact (`version, commitSha, branch, imageReference, imageDigest?, ciProvider, ciRunId, ciRunUrl, buildStatus, testStatus, testSummary`) | `key:artifacts:write` or `artifacts:write` |
| GET | `/projects/{slug}/artifacts` | List (`?branch=&sort=-createdAt`) | `artifacts:read` |
| GET | `/artifacts/{id}` | Detail incl. scans summary, deployments per environment | `artifacts:read` |
| PATCH | `/artifacts/{id}` | Backfill `imageDigest` only | `key:artifacts:write` |
| POST | `/artifacts/{id}/scans` | Upload scan report (multipart) | `key:scans:write` or `scans:write` |
| GET | `/artifacts/{id}/scans` | Scans with summaries | `scans:read` |
| GET | `/scans/{id}` | Scan detail | `scans:read` |
| GET | `/scans/{id}/report` | Download raw report | `scans:read` |
| GET | `/scans/{id}/findings` | Paginated findings (`?severity=&status=&search=`) | `findings:read` |
| GET | `/projects/{slug}/findings` | Findings across latest artifact (`?severity=&scanner=&status=`) | `findings:read` |
| GET | `/findings/{id}` | Finding detail | `findings:read` |
| POST | `/findings/{id}/suppress` | `{ reason, expiresAt? }` → suppression by fingerprint | `findings:suppress` |
| DELETE | `/findings/{id}/suppress` | Unsuppress | `findings:suppress` |
| POST | `/findings/{id}/ai-explain` | AI explanation via gateway | `findings:read` + `ai:chat` |

### 2.6 Deployments & approvals

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| POST | `/deployments` | Request `{ artifactId, environmentId, reason? }` → `202` | `deployments:request` / `key:deployments:request` |
| GET | `/deployments` | List (`?projectId=&environmentId=&status=&requestedBy=&from=&to=`) | `deployments:read` |
| GET | `/deployments/{id}` | Detail: status, artifact, environment, evaluation summary, approvals, counts | `deployments:read` |
| GET | `/deployments/{id}/events` | Timeline (`?afterSequence=` for incremental) | `deployments:read` |
| GET | `/deployments/{id}/evaluation` | Full rule-by-rule explanation | `deployments:read` |
| POST | `/deployments/{id}/approvals` | `{ decision: Approved\|Rejected, comment }` | `deployments:approve` (+ resource handler) |
| POST | `/deployments/{id}/cancel` | Cancel while Requested/AwaitingApproval | `deployments:cancel` |
| POST | `/deployments/{id}/rollback` | Create rollback deployment | `deployments:request` |
| POST | `/deployments/{id}/ai-summary` | AI summary for approvers | `deployments:read` + `ai:chat` |
| GET | `/approvals/pending` | Queue for the caller (deployments awaiting approval they may approve) | `deployments:approve` |

### 2.7 Policies

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| GET | `/policies` | List (`?scope=&scopeId=&tier=`) | `policies:read` |
| POST | `/policies` | Create `{ name, description, scope, scopeId?, appliesToTiers[], rules[] }` | `policies:write` (+ scope handler) |
| GET | `/policies/{id}` | Detail with rules | `policies:read` |
| PUT | `/policies/{id}` | Replace metadata + rules (version++) | `policies:write` |
| PATCH | `/policies/{id}` | `{ isEnabled }` | `policies:write` |
| DELETE | `/policies/{id}` | Soft-delete (disable + archive) | `policies:write` |
| GET | `/policies/rule-types` | Rule type catalogue with JSON schema of parameters (drives the UI editor) | `policies:read` |
| POST | `/policies/simulate` | `{ artifactId, environmentId }` → dry-run evaluation without creating a deployment | `policies:read` |

### 2.8 AI gateway

See [08 AI gateway §6](08-ai-gateway.md#6-endpoints).

### 2.9 Audit

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| GET | `/audit` | Paginated (`?actorId=&action=&resourceType=&resourceId=&outcome=&from=&to=`) | `audit:read` |
| GET | `/audit/{id}` | Detail incl. metadata | `audit:read` |
| GET | `/audit/export` | CSV/NDJSON export for a range (max 30 days) | `audit:read` |

### 2.10 Dashboard & system

| Method | Path | Purpose | Auth |
| --- | --- | --- | --- |
| GET | `/dashboard/summary` | Counts: deployments by status (7d), pending approvals, open findings by severity, AI requests (24h) | authenticated |
| GET | `/dashboard/activity` | Recent deployment events across visible projects | authenticated |
| GET | `/health` | Liveness (`/health/live`) and readiness (`/health/ready`: db, redis; ollama as degraded) | anonymous |
| GET | `/metrics` | Prometheus exposition | internal network only |
| GET | `/openapi/v1.json`, `/scalar` | API docs | anonymous in Development, authenticated otherwise |

## 3. Key payloads

### `POST /deployments` → `202`

```json
{
  "id": "0190a7e1-…",
  "status": "Requested",
  "project": { "id": "…", "slug": "circuit-service", "name": "Circuit Service" },
  "environment": { "id": "…", "name": "Production", "tier": "Production" },
  "artifact": { "id": "…", "version": "v1.8.2", "commitSha": "9f2c…", "branch": "main" },
  "requestedBy": { "type": "ApiKey", "display": "ci-circuit-service" },
  "requestedAt": "2026-09-24T06:31:02Z",
  "links": { "self": "/api/v1/deployments/0190a7e1-…", "events": "…/events", "evaluation": "…/evaluation" }
}
```

### `GET /deployments/{id}`

Adds `approvalsRequired`, `approvalsReceived`, `approvals[]`, `evaluation: { decision,
passed, failed, warnings }`, `startedAt`, `completedAt`, `failureReason`, `durationSeconds`.

### `POST /projects/{slug}/artifacts`

```json
{
  "version": "v1.8.2",
  "commitSha": "9f2c0a1e…",
  "branch": "main",
  "imageReference": "ghcr.io/rith/circuit-service:v1.8.2",
  "imageDigest": "sha256:…",
  "ciProvider": "GitHubActions",
  "ciRunId": "12345678",
  "ciRunUrl": "https://github.com/rith/circuit-service/actions/runs/12345678",
  "buildStatus": "Passed",
  "testStatus": "Passed",
  "testSummary": { "total": 128, "passed": 128, "failed": 0, "skipped": 0, "durationSeconds": 41.2 }
}
```

## 4. SignalR hub contract

Hub path: `/hubs/ops` (JWT via `access_token` query param on negotiate, standard SignalR).

### Groups

| Group | Joined by | Receives |
| --- | --- | --- |
| `user:{userId}` | automatically on connect | personal notifications (approval requests, own deployments) |
| `project:{projectId}` | `JoinProject(projectId)` after authorization | deployment/scan events of that project |
| `deployment:{deploymentId}` | `JoinDeployment(id)` | timeline events for one deployment |
| `approvers` | on connect if caller has `deployments:approve` | new approval requests |

### Server → client messages

| Message | Payload | Trigger |
| --- | --- | --- |
| `DeploymentCreated` | deployment summary | new request |
| `DeploymentStatusChanged` | `{ deploymentId, projectId, from, to, at }` | any transition |
| `DeploymentEventAppended` | `DeploymentEvent` | timeline growth |
| `ApprovalRequested` | `{ deploymentId, project, environment, artifactVersion, approvalsRequired }` | evaluation → AwaitingApproval |
| `ScanCompleted` | `{ artifactId, scanner, summary }` | ingestion |
| `Notification` | `{ level, title, message, link }` | generic toast |

Client reaction is uniform: show toast if relevant, then
`queryClient.invalidateQueries({ queryKey })` for the affected resource. The UI never
mutates cache from SignalR payloads directly (keeps API as the single source of truth).

### Client → server

`JoinProject(projectId)`, `LeaveProject(projectId)`, `JoinDeployment(id)`, `LeaveDeployment(id)`.
Each join is authorized with the same resource handlers as the REST endpoints.

## 5. Webhooks (outbound, Phase 5)

`POST <team webhook url>` with headers `X-AegisOps-Event`, `X-AegisOps-Signature`
(`sha256=` HMAC of body) and body `{ event, occurredAt, deployment: {…} }`. Retries: 3
attempts with backoff via `SendNotification` job.
