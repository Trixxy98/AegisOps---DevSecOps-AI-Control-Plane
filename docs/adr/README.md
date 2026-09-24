# Architecture Decision Records

ADRs capture *why* a significant decision was made, the alternatives considered and the
consequences accepted. They are immutable once `Accepted`; to change course, write a new
ADR that supersedes the old one.

## Index

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-aspnet-core-clean-architecture.md) | ASP.NET Core with Clean Architecture for the backend | Accepted |
| [0002](0002-modular-monolith.md) | Modular monolith with a separate Worker process | Accepted |
| [0003](0003-postgresql-source-of-truth.md) | PostgreSQL as the single source of truth | Accepted |
| [0004](0004-redis-for-ephemeral-state.md) | Redis only for ephemeral and high-frequency state | Accepted |
| [0005](0005-durable-job-queue-in-postgresql.md) | Durable job queue in PostgreSQL instead of a message broker | Accepted |
| [0006](0006-ollama-local-llm.md) | Ollama as the only AI provider behind a gateway abstraction | Accepted |
| [0007](0007-github-actions-ci-aegisops-control-plane.md) | GitHub Actions performs CI; AegisOps is the control plane | Accepted |
| [0008](0008-target-applications.md) | Target applications: three network-flavoured demo services | Accepted |
| [0009](0009-separate-repositories-for-targets.md) | Separate repositories for target applications | Accepted |
| [0010](0010-sarif-normalization.md) | SARIF as the normalization format for scanner results | Accepted |
| [0011](0011-policy-engine-most-restrictive-wins.md) | Policy union with most-restrictive-wins semantics | Accepted |

## Template

```markdown
# ADR-XXXX — Title

- **Status:** Proposed | Accepted | Superseded by ADR-YYYY
- **Date:** YYYY-MM-DD

## Context
## Decision
## Alternatives considered
## Consequences
```
