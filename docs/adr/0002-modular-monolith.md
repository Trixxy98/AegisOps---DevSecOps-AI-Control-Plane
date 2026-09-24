# ADR-0002 — Modular monolith with a separate Worker process

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

AegisOps has clearly separable concerns (identity, organization, artifacts/security,
policies, deployments, AI, audit, jobs). It also has long-running work (scans, container
deployments) that must not run inside HTTP requests. A solo developer must be able to run,
debug and deploy the whole system easily.

## Decision

- One codebase, one `Domain`/`Application`/`Infrastructure`, organized as **modules by
  folder** with their own PostgreSQL **schema** and public Application interfaces.
- Modules communicate in-process through Application interfaces and domain events; they do
  not access each other's entities directly (architecture-tested).
- Two runtime processes share the same assemblies: **`AegisOps.Api`** (HTTP + SignalR) and
  **`AegisOps.Worker`** (job processing). Both can be scaled independently later.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Microservices per module | Multiplies auth, deployment, observability and data-consistency work; no benefit at this scale |
| Single process (worker inside API) | Simpler, but couples API availability to heavy jobs and Docker socket access to the user-facing process (security) |
| Separate assemblies per module | Cleaner boundaries, but slows a solo developer; folders + architecture tests give most of the benefit |

## Consequences

- Positive: one DbContext and transaction per use case; refactoring across modules is
  cheap; still demonstrates boundaries convincingly.
- Negative: module discipline relies on tests and reviews rather than compiler barriers.
- The Worker needs a channel to the Api for real-time messages → Redis pub/sub bridge
  ([02 §6.3](../02-architecture.md#63-real-time-event-bridge)).
