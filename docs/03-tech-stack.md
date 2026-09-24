# 03 — Tech Stack

Status legend: `DECIDED` locked for v1 · `PROPOSED` needs confirmation · `V2` deferred.

## 1. Summary

```
FRONTEND                         BACKEND                          DATA
├── React 19                     ├── C# / .NET 10 (LTS)           ├── PostgreSQL 17
├── TypeScript 5                 ├── ASP.NET Core 10              └── Redis 7 (or Valkey)
├── Vite                         ├── EF Core 10 + Npgsql
├── Tailwind CSS 4               ├── ASP.NET Core Identity        ASYNC / REALTIME
├── TanStack Query 5             ├── JWT bearer auth              ├── BackgroundService (Worker)
├── React Router 7               ├── SignalR                      ├── PostgreSQL job table
├── Axios                        ├── FluentValidation             └── SignalR + Redis pub/sub
├── @microsoft/signalr           ├── Serilog
├── react-hook-form + zod        ├── StackExchange.Redis          DEVSECOPS
└── lucide-react, recharts       ├── Docker.DotNet                ├── Docker + Compose
                                 └── OpenTelemetry                ├── GitHub Actions
                                                                  ├── Gitleaks
TESTING                          AI                               ├── Trivy
├── xUnit                        └── Ollama (local LLM)           └── Semgrep
├── Testcontainers
├── NSubstitute                  OBSERVABILITY
├── NetArchTest                  ├── OpenTelemetry SDK
├── Vitest + Testing Library     ├── Prometheus
└── Playwright (V2)              └── Grafana
```

## 2. Frontend

| Technology | Status | Why |
| --- | --- | --- |
| **React 19 + TypeScript** | DECIDED | Already known; TypeScript catches API-contract drift early |
| **Vite** | DECIDED | Fast dev server, simple production build served by nginx |
| **Tailwind CSS 4** | DECIDED | Utility-first, no design-system overhead for a solo project |
| **TanStack Query 5** | DECIDED | Most screens display asynchronous server state (deployments, scans, approvals). Caching, invalidation and background refetch are exactly what is needed |
| **React Router 7** | DECIDED | Standard client routing; nested layouts for `/projects/:slug/*` |
| **Axios** | DECIDED | Interceptors for JWT attach + silent refresh + Problem Details parsing |
| **@microsoft/signalr** | DECIDED | Live deployment timeline and approval notifications; on event → `queryClient.invalidateQueries` |
| **react-hook-form + zod** | DECIDED | Policy rule editor and forms with typed validation |
| **lucide-react** | DECIDED | Icons |
| **recharts** | DECIDED | Dashboard charts (deployments/day, findings by severity, AI usage) |
| shadcn/ui-style components | PROPOSED | Copy-in components on top of Tailwind; avoids writing every dialog/table from scratch |
| **Vitest + Testing Library** | DECIDED | Unit/component tests |
| Playwright | V2 | End-to-end tests for the four journeys |

## 3. Backend

| Technology | Status | Why |
| --- | --- | --- |
| **.NET 10 / C#** | DECIDED | Current LTS; primary learning target. See [ADR-0001](adr/0001-aspnet-core-clean-architecture.md) |
| **ASP.NET Core Minimal APIs** | DECIDED | Endpoint-per-file organization fits Clean Architecture; less ceremony than controllers; filters for validation |
| **Clean Architecture + Modular Monolith** | DECIDED | Enforced layering, module folders, one deployable. See [ADR-0002](adr/0002-modular-monolith.md) |
| **EF Core 10 + Npgsql** | DECIDED | Migrations, JSONB mapping, schema-per-module, optimistic concurrency |
| **ASP.NET Core Identity** | DECIDED | Battle-tested user store, password hashing, lockout; we expose it via JWT, not cookies |
| **JWT bearer + refresh tokens** | DECIDED | Stateless API auth for SPA; refresh via httpOnly cookie with rotation |
| **SignalR** | DECIDED | Real-time timeline & notifications without polling |
| **BackgroundService (Worker project)** | DECIDED | Long-running work outside the request path. See [ADR-0005](adr/0005-durable-job-queue-in-postgresql.md) |
| **FluentValidation** | DECIDED | Expressive request validation; Apache-2.0 |
| **Serilog** | DECIDED | Structured logging with enrichers (correlation id, user id) |
| **StackExchange.Redis** | DECIDED | Rate limiting, idempotency, locks, pub/sub |
| **Docker.DotNet** | DECIDED | Deployment executor talks to Docker Engine API (pull, create, start, health) |
| **Microsoft.AspNetCore.OpenApi + Scalar** | DECIDED | OpenAPI document generation and a modern API reference UI |
| **OpenTelemetry .NET** | DECIDED | Metrics + traces; Prometheus exporter |
| MediatR / AutoMapper | REJECTED | Both moved to commercial licensing; explicit application services and hand-written mapping are simpler to read and debug |
| Hangfire / Quartz | REJECTED (v1) | A 100-line job table with `SKIP LOCKED` teaches more and adds no dependency; can revisit |

## 4. Data

| Technology | Status | Why |
| --- | --- | --- |
| **PostgreSQL 17** | DECIDED | Relational model (Team → Project → Environment → Deployment → Scan), JSONB for rule parameters and evidence, schemas per module, `SKIP LOCKED` for the job queue. See [ADR-0003](adr/0003-postgresql-source-of-truth.md) |
| **Redis 7** (or **Valkey**) | DECIDED | Sliding-window rate limits, idempotency keys, caches, distributed locks, pub/sub bridge. Never the source of truth. See [ADR-0004](adr/0004-redis-for-ephemeral-state.md) |

> Interview line: *"PostgreSQL is my source of truth; Redis holds short-lived, high-frequency state that I can afford to lose."*

## 5. DevSecOps, AI, observability

| Technology | Status | Role |
| --- | --- | --- |
| **Docker + Docker Compose** | DECIDED | Local platform and target "environments"; one-command demo |
| **GitHub Actions** | DECIDED | CI for AegisOps itself and for every target repository; reports into AegisOps. See [ADR-0007](adr/0007-github-actions-ci-aegisops-control-plane.md) |
| **GitHub Container Registry (GHCR)** | DECIDED | Free image hosting for target services |
| **Gitleaks** | DECIDED | Secret detection on source |
| **Trivy** | DECIDED | Container image + dependency + misconfiguration scanning |
| **Semgrep** (OSS rules) | DECIDED | SAST for insecure code patterns |
| **SARIF** as normalization format | DECIDED | All three scanners emit SARIF → one parser. See [07 Security scanning](07-security-scanning.md) |
| **Ollama** | DECIDED | Free local LLM serving; OpenAI-compatible-ish HTTP API. See [ADR-0006](adr/0006-ollama-local-llm.md) |
| Default models | PROPOSED | `llama3.2:3b` (chat, fits 8 GB RAM), `qwen2.5-coder:7b` (code/security summaries, optional) |
| **OpenTelemetry + Prometheus + Grafana** | DECIDED (Phase 7) | Metrics, dashboards; traces exported to console/OTLP in dev |

## 6. Explicitly excluded for v1

| Excluded | Why not now | When it would make sense |
| --- | --- | --- |
| Kubernetes / Helm | Adds a cluster to run, learn and debug; v1 environments are single-host containers | When the executor abstraction needs a second implementation |
| Kafka / RabbitMQ / MassTransit | Job volume is tiny; a durable table with `SKIP LOCKED` is sufficient and simpler to reason about | If multiple worker types or fan-out consumers appear |
| Terraform / cloud accounts | Costs money; infra provisioning is not the story of this project | Never for the portfolio version |
| Microservices for AegisOps itself | Would multiply deployment, auth and observability work by N | Never; the modular monolith is the point |
| Multiple AI providers | Governance logic is provider-agnostic behind `ILlmClient`; one adapter proves it | Add OpenAI-compatible adapter as a small V2 item |
| LangChain / agent frameworks / vector DB | Not needed for a gateway; RAG is a different product | Never in scope |
| Multiple databases | PostgreSQL covers relational + JSONB + queue | Never |
| Mobile app | Out of scope | Never |

## 7. Version policy

- Track **LTS** for .NET (10.x) and Node (22.x LTS for building the frontend).
- Pin **major** versions in `Directory.Packages.props` (central package management) and
  `package.json`; let Dependabot propose minor/patch bumps weekly.
- Container base images pinned by tag *and* refreshed by Dependabot (`docker` ecosystem).
- Scanners run via their official Docker images / GitHub Actions at pinned major versions.
