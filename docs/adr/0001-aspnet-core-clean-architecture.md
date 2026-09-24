# ADR-0001 — ASP.NET Core with Clean Architecture for the backend

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

AegisOps is a solo portfolio project whose secondary goal is to build deep, interview-ready
competence in a mainstream enterprise backend stack. The backend must handle
authentication/authorization, background processing, real-time messaging, relational
persistence and integration with external tools (Docker, scanners, an LLM).

## Decision

Build the backend in **C# on .NET 10 (LTS) with ASP.NET Core**, structured as
**Clean Architecture**: `Domain` → `Application` → `Infrastructure`, with `Api` and `Worker`
as thin composition roots. Minimal APIs are used for endpoints. Dependency direction is
enforced by architecture tests.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Node.js/Express + Prisma | Already familiar; offers less learning value; weaker story for background services, typed authorization and real-time in one framework |
| Laravel | Strong CRUD productivity, but the project's value is in policy logic, workers and real-time, where .NET's hosted services and SignalR fit better |
| Go | Excellent for services, but less integrated auth/identity/ORM story; the goal includes ASP.NET Core specifically |
| Vertical slice without layers | Faster initially; harder to keep the policy engine pure and to enforce module boundaries as the project grows |

## Consequences

- Positive: first-class `BackgroundService`, SignalR, Identity, EF Core, OpenTelemetry;
  strong typing across the domain; clear interview narrative around layering and DI.
- Negative: more ceremony than a script-style backend; .NET containers are larger than
  Node's; requires discipline to keep `Application` free of infrastructure concerns.
- Mitigation: architecture tests, endpoint templates and conventions in
  [18](../18-repository-structure-and-conventions.md).
