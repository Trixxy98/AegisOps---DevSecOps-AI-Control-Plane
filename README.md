# AegisOps — DevSecOps & AI Control Plane

> **Status:** Early implementation. The Development Compose stack supports an end-to-end
> no-op deployment (request → policy evaluation → worker execution → timeline). The
> local `site-service` Staging Docker target is opt-in through an existing project and
> published artifact; production operation is not supported by this quick start.
> See [`docs/README.md`](docs/README.md) for the design.

AegisOps is a self-hosted **control plane** that sits between CI and your environments.
It does not build code (GitHub Actions does that). It decides **whether a change may
reach an environment**, and it governs **how people and services use AI models** — with a
complete audit trail for both.

```
                GitHub Actions (CI)                         Users
        build · test · gitleaks · trivy · semgrep             │
                        │                                     │
                        ▼                                     ▼
┌──────────────────────────────────────────────────────────────────────┐
│                              AegisOps                                │
│                                                                      │
│   Artifacts ─► Security Scans ─► Policy Engine ─► Approvals ─► Deploy │
│                                                                      │
│   AI Gateway: model access · rate limit · sensitive-data · audit     │
└──────────────────────────────────────────────────────────────────────┘
                        │                                     │
                        ▼                                     ▼
          Target services (staging / production)        Ollama (local LLM)
```

## What AegisOps governs

| Plane | Question it answers | Result |
| --- | --- | --- |
| **Deployment governance** | "May artifact `v1.8.2` of `circuit-service` reach **Production**?" | `ALLOW` / `REQUIRE_APPROVAL` / `DENY` with a rule-by-rule explanation |
| **AI governance** | "May this user send this prompt to this model right now?" | Forwarded, redacted, or blocked — always audited |

## Target applications

AegisOps manages a small fleet of independent demo services (each in its own repository
with its own CI pipeline). They exist to be built, scanned, evaluated and deployed:

| Service | Purpose |
| --- | --- |
| `site-service` | Network site & device inventory |
| `circuit-service` | Circuit / link management |
| `notification-service` | Outbound notifications (email/webhook stubs) |

See [`docs/13-target-applications.md`](docs/13-target-applications.md).

## Locked tech stack (v1)

| Area | Choice |
| --- | --- |
| Frontend | React · TypeScript · Tailwind CSS · TanStack Query · Axios · SignalR client |
| Backend | C# · ASP.NET Core 10 · EF Core · ASP.NET Core Identity · JWT · SignalR |
| Architecture | Clean Architecture · Modular Monolith · separate Worker process |
| Data | PostgreSQL (source of truth) · Redis (ephemeral / high-frequency state) |
| DevOps | Docker · Docker Compose · GitHub Actions |
| Security scanners | Gitleaks · Trivy · Semgrep |
| AI | Ollama (local LLM) |
| Observability | OpenTelemetry · Prometheus · Grafana |
| Testing | xUnit · Testcontainers · integration tests · Vitest |

Deliberately **out of scope for v1**: Kubernetes, Kafka/RabbitMQ, Terraform, cloud
providers, LangChain, multiple AI providers. See [`docs/03-tech-stack.md`](docs/03-tech-stack.md).

## Documentation map

| # | Document | What it covers |
| --- | --- | --- |
| 01 | [Vision & scope](docs/01-vision-and-scope.md) | Problem, goals, non-goals, personas, success criteria |
| 02 | [Architecture](docs/02-architecture.md) | Context/container diagrams, modules, layering, key flows |
| 03 | [Tech stack](docs/03-tech-stack.md) | Every technology, why it was chosen, what is excluded |
| 04 | [Domain model](docs/04-domain-model.md) | Entities, enums, relationships, state machines |
| 05 | [Policy engine](docs/05-policy-engine.md) | Rule types, evaluation algorithm, explainability |
| 06 | [Deployment pipeline](docs/06-deployment-pipeline.md) | End-to-end lifecycle, worker jobs, executors |
| 07 | [Security scanning](docs/07-security-scanning.md) | Gitleaks/Trivy/Semgrep, SARIF normalization, severities |
| 08 | [AI gateway](docs/08-ai-gateway.md) | Governance pipeline, rate limits, sensitive-data detection |
| 09 | [Identity & authorization](docs/09-identity-and-authorization.md) | Roles, permissions, policy-based authorization, API keys |
| 10 | [API specification](docs/10-api-specification.md) | REST endpoints, conventions, SignalR hub contract |
| 11 | [Database design](docs/11-database-design.md) | Schemas, tables, indexes, migrations, seeding |
| 12 | [Frontend](docs/12-frontend.md) | Pages, routing, data layer, realtime, structure |
| 13 | [Target applications](docs/13-target-applications.md) | The demo services AegisOps governs |
| 14 | [DevOps & infrastructure](docs/14-devops-and-infrastructure.md) | Docker Compose, Dockerfiles, GitHub Actions workflows |
| 15 | [Observability](docs/15-observability.md) | Metrics, traces, logs, dashboards, health checks |
| 16 | [Testing strategy](docs/16-testing-strategy.md) | Test pyramid, PolicyEngine test matrix, integration tests |
| 17 | [Security hardening](docs/17-security-hardening.md) | Threat model and controls for AegisOps itself |
| 18 | [Repository structure & conventions](docs/18-repository-structure-and-conventions.md) | Solution layout, coding standards, Git workflow |
| 19 | [Roadmap](docs/19-roadmap.md) | Phases 0–8, deliverables, definition of done |
| 20 | [Glossary](docs/20-glossary.md) | Terminology |
| — | [ADRs](docs/adr/README.md) | Architecture decision records |

## Local quick start (Development only)

Requires Docker Compose. The API migrates the local database on startup and seeds demo
users, a team, and policies. Use a fresh random JWT signing key (32+ bytes); never use
the `.env.example` value outside local development.

```bash
cp .env.example .env
# Edit JWT_SIGNING_KEY in .env before starting.
docker compose up --build -d
docker compose logs api | grep 'Development user admin@aegisops.local created'
# Copy the printed password (only generated on first startup) and open http://localhost:3000
AEGISOPS_ADMIN_PASSWORD='<password from logs>' scripts/smoke-compose-deployment.sh
```

The smoke test uses the Web proxy to sign in, create a project/artifact, request a
Development deployment, and assert the worker recorded an Allow evaluation and
Succeeded event and updated the environment's current artifact. Each run creates a
uniquely named test project in the local database. If the database already exists, the
seed password is not regenerated: keep the first-run password or reset the local database deliberately.
The API is also exposed at `http://localhost:8080`; Web is at `http://localhost:3000`.

This path uses the **noop** executor; it does not start a target container. For the
existing `site-service` project only, the Development seed changes its **Staging**
environment from noop to Docker (`site-service-staging`, network `aegisops-staging`,
host port 8101, container port 8080, `/health`). It never changes Production or an
operator-configured target. Staging requires three fresh security scans and passing
tests. Register a real CI artifact from `ghcr.io/trixxy98/site-service` before
requesting Staging; `ghcr.io/aegisops/site-service:latest` in the design-only
`deploy/docker-compose.targets.yml` is not a published image. That file is **not**
part of this Compose startup; do not start its Production services. The target image
currently publishes linux/amd64 only; the executor retries with that platform when
the default pull fails on arm64.

Compose mounts the host Docker socket **only into the Worker** so it can manage
containers. `DOCKER_HEALTH_HOST=host.docker.internal` lets the Worker probe host
published port 8101 (its own loopback is not the Docker host). This assumes Docker
Desktop; another Docker host needs an operator-configured reachable address. The
API does not have socket access, so its Docker runtime-inspection endpoint may be
unavailable, while deployment timeline and host `/health` and `/version` remain usable.
The Docker socket grants effective root-equivalent control over the host Docker daemon
and all containers/volumes, despite being mounted only in the Worker. Run this
Development stack only on a trusted local machine, restrict access to AegisOps
deployment APIs, and never use this pattern in Production without isolating the daemon
or introducing a constrained Docker authorization proxy. The executor refuses to
replace an unlabeled/foreign named container or remove an existing `-previous` backup;
verify target names and port 8101 are free before requesting a deployment. It does not
auto-deploy during Compose startup. Check wiring with
`python3 tests/staging-docker-contract.py`. Do not point this stack at production.

SARIF uploads larger than 2 MB are queued for parsing by the worker. Compose mounts a
dedicated persistent `reports` volume at `/app/reports` in both API and worker, separate
from `pgdata`. Before upgrading an existing stack, preserve any pending reports from the
old API container's `/app/reports` (or re-upload those reports after upgrading); an
existing container's private filesystem is **not** migrated automatically into the new
volume. Do not run `docker compose down -v`: it deletes the database and reports volumes.
Check the mount wiring with `python3 tests/report-storage-contract.py`.

## License

MIT (to be added with the first code commit).
