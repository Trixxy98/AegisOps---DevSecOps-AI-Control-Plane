# AegisOps Documentation

This folder is the single source of truth for **what AegisOps is, how it is designed, and
in which order it will be built**. Every implementation phase should start by reading the
relevant document here and end by updating it if the design changed.

## How to read these documents

| If you want to… | Read |
| --- | --- |
| Understand the product in 10 minutes | [01 Vision & scope](01-vision-and-scope.md), [02 Architecture](02-architecture.md) |
| Know why a technology was picked (or rejected) | [03 Tech stack](03-tech-stack.md), [ADRs](adr/README.md) |
| Implement the core domain | [04 Domain model](04-domain-model.md), [11 Database design](11-database-design.md) |
| Implement the "brain" of the system | [05 Policy engine](05-policy-engine.md) |
| Implement the deployment flow | [06 Deployment pipeline](06-deployment-pipeline.md), [07 Security scanning](07-security-scanning.md) |
| Implement AI governance | [08 AI gateway](08-ai-gateway.md) |
| Implement auth | [09 Identity & authorization](09-identity-and-authorization.md) |
| Build the frontend or consume the API | [10 API specification](10-api-specification.md), [12 Frontend](12-frontend.md) |
| Set up CI/CD, Docker, monitoring | [13 Target applications](13-target-applications.md), [14 DevOps](14-devops-and-infrastructure.md), [15 Observability](15-observability.md) |
| Write tests | [16 Testing strategy](16-testing-strategy.md) |
| Secure the platform itself | [17 Security hardening](17-security-hardening.md) |
| Know where files go and how to name things | [18 Repository structure & conventions](18-repository-structure-and-conventions.md) |
| Know what to build next | [19 Roadmap](19-roadmap.md) |
| Look up a term | [20 Glossary](20-glossary.md) |

## Document conventions

- **Decision status.** Anything marked `DECIDED` is locked for v1. Anything marked
  `PROPOSED` still needs the owner's confirmation. Anything marked `V2` is intentionally
  deferred.
- **Names are canonical.** Entity names, enum values, status names, endpoint paths and
  metric names in these documents are the names to use in code. If a name must change,
  change it here first.
- **Diagrams** are written in Mermaid so they render on GitHub.
- **ADRs** live in [`adr/`](adr/README.md) and are immutable once accepted; supersede them
  with a new ADR instead of editing.

## Decisions confirmed 2026-09-24

| Topic | Decision | Where |
| --- | --- | --- |
| Domain of the target applications | Network/infrastructure services: `site-service`, `circuit-service`, `notification-service` | [ADR-0008](adr/0008-target-applications.md) |
| Repository layout for targets | One GitHub repository per target service, plus a template repo | [ADR-0009](adr/0009-separate-repositories-for-targets.md) |

## Still proposed

| Topic | Current proposal | Where |
| --- | --- | --- |
| Default local LLM | `llama3.2:3b` for chat, `qwen2.5-coder:7b` optional for code tasks | [08 AI gateway](08-ai-gateway.md) |
| GitHub owner / exact repository names | Platform repo `AegisOps`; targets `aegisops-<service>` | [ADR-0009](adr/0009-separate-repositories-for-targets.md) |
