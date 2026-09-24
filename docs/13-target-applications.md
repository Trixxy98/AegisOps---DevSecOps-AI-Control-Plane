# 13 — Target Applications

The target applications are **the things AegisOps governs**. They are deliberately tiny:
their only job is to be built, scanned, evaluated and deployed. Domain decision in
[ADR-0008](adr/0008-target-applications.md) (Accepted — network/infrastructure flavour).

```
AegisOps (control plane)
    │
    ├── site-service          — network sites & devices inventory
    ├── circuit-service       — circuits / links between sites
    └── notification-service  — outbound notifications (email/webhook stubs)
```

## 1. Design rules for targets

| Rule | Reason |
| --- | --- |
| Each service in its **own GitHub repository** (`aegisops-<name>`) | Own CI pipeline, own API key, own `Repository` row — mirrors reality ([ADR-0009](adr/0009-separate-repositories-for-targets.md)) |
| ASP.NET Core **Minimal API**, in-memory store, no database | Zero infra; the service is a deployment target, not a product |
| Mandatory endpoints: `GET /health`, `GET /version`, `GET /` | Executor health check; version visible after deploy; friendly landing page |
| Listens on `8080`; config via env vars | Uniform executor config |
| Multi-stage `Dockerfile`, non-root user, `HEALTHCHECK` | Trivy misconfiguration checks stay green on `main` |
| SemVer tags `vX.Y.Z` drive artifact versions | Clear "what is running where" |
| A `demo/vulnerable` branch | Produces findings on demand (§5) |
| Generated from **one template repo** (`aegisops-service-template`) | Consistency; 3 services in an afternoon |

## 2. The services

### `site-service`

| Endpoint | Purpose |
| --- | --- |
| `GET /sites` · `GET /sites/{code}` · `POST /sites` | Sites: `code` (e.g. `KUL-01`), `name`, `address`, `region` |
| `GET /sites/{code}/devices` · `POST /sites/{code}/devices` | Devices: `hostname`, `role` (router/switch/firewall), `vendor`, `managementIp` |
| `GET /health`, `GET /version` | Standard |

### `circuit-service`

| Endpoint | Purpose |
| --- | --- |
| `GET /circuits` · `GET /circuits/{id}` · `POST /circuits` | Circuits: `circuitId` (`CKT-0001`), `aSite`, `zSite`, `provider`, `bandwidthMbps`, `status` |
| `POST /circuits/{id}/status` | Change `Active/Planned/Decommissioned` |
| `GET /health`, `GET /version` | Standard |

### `notification-service`

| Endpoint | Purpose |
| --- | --- |
| `POST /notifications` | `{ channel: email\|webhook, to, subject, body }` → stores and "sends" (logs) |
| `GET /notifications` | Sent log |
| `GET /health`, `GET /version` | Standard |

Optional polyglot twist (V2): implement `notification-service` in Node.js/Express to show
that scanners and the policy engine are language-agnostic.

## 3. `GET /version` contract

```json
{
  "service": "circuit-service",
  "version": "v1.8.2",
  "commitSha": "9f2c0a1e",
  "buildTime": "2026-09-24T05:58:10Z",
  "environment": "Staging"
}
```

Values are injected at build time (`--build-arg VERSION=… COMMIT_SHA=…`) and at run time
(`ASPNETCORE_ENVIRONMENT` from `Environment.Target.env`).

## 4. Template repository layout

```
aegisops-service-template/
├── .github/workflows/ci.yml          # build → test → image → scans → register → request deploy
├── src/Service/                      # Program.cs, Endpoints/, Models/, Store/
├── tests/Service.Tests/              # a handful of endpoint tests (WebApplicationFactory)
├── Dockerfile
├── .dockerignore
├── .gitleaks.toml                    # allowlist for test fixtures
├── .semgrepignore
├── README.md
└── VERSION                           # optional, tags are the source of truth
```

### CI workflow (`ci.yml`) — outline

```yaml
on:
  push: { branches: [main, 'demo/**'], tags: ['v*'] }
  pull_request:
  workflow_dispatch:
    inputs: { environment: { type: choice, options: [Staging, Production] } }

jobs:
  build:      # dotnet restore/build/test → test summary JSON artifact
  image:      # buildx → ghcr.io/<owner>/<service>:<version> (+ digest output)
  security:   # gitleaks, semgrep, trivy → SARIF files (exit-code 0)
  register:   # POST /projects/{slug}/artifacts  → ARTIFACT_ID
              # POST /artifacts/{id}/scans ×3
  deploy:     # POST /deployments → Staging (on main/tags), Production (workflow_dispatch)
```

Secrets/vars per repo: `AEGISOPS_URL` (var), `AEGISOPS_API_KEY` (secret), `GITHUB_TOKEN`
(built-in, for GHCR). The AegisOps API key is scoped to the corresponding project with
`artifacts:write, scans:write, deployments:request`.

Version resolution: tag `vX.Y.Z` → that version; push to `main` without tag →
`v0.0.0-main.<short-sha>` (pre-release; policies for Production can require `refs/tags/v*`).

Production requests from CI (`workflow_dispatch` → Production) only work if the target
environment has `Target.allowApiKeyProduction = true`; by default a Production deployment
is requested by a human from the AegisOps UI ("Promote to Production" on the artifact) and
then goes through the normal approval flow.

## 5. Vulnerable demo branch

Branch `demo/vulnerable` in each target repository adds — intentionally and clearly
labelled — one trigger per scanner:

| Scanner | Trigger | Expected finding |
| --- | --- | --- |
| Gitleaks | `appsettings.Demo.json` with `"AwsAccessKey": "AKIAIOSFODNN7EXAMPLE"` | 1 Critical secret |
| Semgrep | Endpoint building SQL via string concatenation from a query param; `MD5` hash use | 1–2 High/Medium |
| Trivy | Base image pinned to an old tag with known CVEs, plus an outdated `System.Text.Json` package reference | several High/Critical |

Result in demos: Staging (`MaxFindings{High,10}` Warn) may pass with warnings, Production
is **denied** — and the UI explains exactly which rule failed.

The branch is rebased regularly and never merged. Its README states it is a demo.

## 6. Local development of targets

Each repo has `docker compose up` for itself (single service). To see the full picture
locally, AegisOps' `deploy/docker-compose.targets.yml` pulls the latest GHCR images and
creates the `aegisops-staging` / `aegisops-production` networks; the Worker then manages
containers on those networks.

## 7. Seed mapping in AegisOps

| Project slug | Team | Repository | Environments (ports staging/prod) |
| --- | --- | --- | --- |
| `site-service` | `network-platform` | `<owner>/aegisops-site-service` | 8101 / 8201 |
| `circuit-service` | `network-platform` | `<owner>/aegisops-circuit-service` | 8102 / 8202 |
| `notification-service` | `network-platform` | `<owner>/aegisops-notification-service` | 8103 / 8203 |

Development environments use the `noop` executor by default (nothing to run locally), so
developers can exercise the flow without images.
