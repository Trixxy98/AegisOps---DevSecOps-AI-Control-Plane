# ADR-0009 — Separate repositories for target applications

- **Status:** Accepted (owner confirmed 2026-09-24)
- **Date:** 2026-09-24

## Context

Target services could live inside the AegisOps monorepo (`targets/`) or in their own
repositories. AegisOps models a `Repository` per `Project` and expects each project to have
its own CI pipeline and API key.

## Decision

Each target service lives in its **own GitHub repository** (`aegisops-site-service`,
`aegisops-circuit-service`, `aegisops-notification-service`), generated from a shared
**template repository** (`aegisops-service-template`). The AegisOps repository contains only:

- the composite action used by target workflows (`.github/actions/aegisops-report`),
- `deploy/docker-compose.targets.yml` that pulls target images from GHCR,
- saved CI payload fixtures for offline replay.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Monorepo with path-filtered workflows | Simpler to browse, but every `Repository` row would point at the same repo; API keys/CI per project become artificial; less realistic demo |
| Monorepo first, split later | Migration work and inconsistent docs mid-project |

## Consequences

- Positive: realistic multi-repo governance; each project has its own artifacts, keys,
  workflows and vulnerable branch; clean interview story.
- Negative: four extra repositories to maintain (three services + template); mitigated by
  the template and by keeping services tiny.
- Naming (`aegisops-*` prefix) and GitHub owner to be confirmed with the owner.
