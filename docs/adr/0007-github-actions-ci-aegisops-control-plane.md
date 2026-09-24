# ADR-0007 — GitHub Actions performs CI; AegisOps is the control plane

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

A portfolio project could try to *be* a CI system (clone, build, test, scan). That is a
large, well-solved problem and would consume the whole schedule. The differentiating value
of AegisOps is deciding **whether** a build may reach an environment and recording **why**.

## Decision

- **GitHub Actions** builds, tests, packages and scans each target repository and AegisOps
  itself.
- CI reports *facts* to AegisOps via API: artifact metadata, test results, SARIF reports —
  and *requests* deployments.
- **AegisOps** owns policy evaluation, approvals, execution and audit. It never clones or
  builds source in v1.
- Scanners in CI run with exit code 0; failing on findings is a policy decision made by
  AegisOps, so every artifact — good or bad — is visible to the control plane.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| AegisOps runs builds and scans itself (Worker-run pipeline) | Re-implements CI; worker-run scanning is kept only as an optional re-scan feature |
| GitHub webhooks (push events) driving AegisOps | Requires public ingress and repo-level configuration; pushing from CI with an API key is simpler and works with self-hosted runners/tunnels |
| Jenkins / GitLab CI | GitHub is where the code lives; Actions is free for public repos |

## Consequences

- Positive: realistic separation of concerns; small, focused AegisOps codebase; easy to
  explain in interviews ("CI does the work; AegisOps makes the decision").
- Negative: AegisOps must be reachable from CI (self-hosted runner, tunnel or offline
  replay — see [14 §4](../14-devops-and-infrastructure.md#4-github-actions--target-repositories)).
