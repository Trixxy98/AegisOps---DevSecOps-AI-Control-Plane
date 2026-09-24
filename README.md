# AegisOps — DevSecOps & AI Control Plane

> **Status:** Planning & documentation phase. No application code has been written yet.
> Start with [`docs/README.md`](docs/README.md).

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

## Planned quick start (after Phase 1)

```bash
git clone https://github.com/<you>/AegisOps.git
cd AegisOps
docker compose up -d
open http://localhost:3000
```

## License

MIT (to be added with the first code commit).
