# ADR-0008 — Target applications: three network-flavoured demo services

- **Status:** Accepted (owner confirmed 2026-09-24)
- **Date:** 2026-09-24

## Context

AegisOps needs concrete services to govern so that deployment and security scenarios are
tangible. "Payment API" in early discussions was only a placeholder. Five domain options
were considered: e-commerce, internal company services, AI applications,
network/infrastructure services, and a generic microservice demo.

## Decision

Build **three small, independent services** as targets (the "microservice demo"
*structure*), using a **network/infrastructure** *domain* that reflects the owner's
professional background:

| Service | Domain |
| --- | --- |
| `site-service` | Network sites and devices inventory |
| `circuit-service` | Circuits/links between sites |
| `notification-service` | Generic outbound notifications |

Each service is a minimal ASP.NET Core API with an in-memory store, `/health` and
`/version`, a multi-stage Dockerfile, its own CI workflow and a `demo/vulnerable` branch.
Their business logic is intentionally trivial — they exist to be deployed and scanned.

## Alternatives considered

| Option | Assessment |
| --- | --- |
| E-commerce (Product/Order/Inventory) | Familiar to interviewers but generic; no personal story |
| Internal company services (Employee/Document/Notification) | Enterprise feel; also generic |
| AI applications as targets | Confuses the story — AegisOps already *is* the AI-governance layer; targets should be ordinary services |
| Generic microservice demo (User/Product/Order) | Right *structure*; domain chosen here is a thin skin over exactly this structure |

## Consequences

- Positive: a coherent narrative ("a platform team governs the services that manage the
  network"), differentiating in interviews; the domain choice costs nothing technically.
- Negative: none material; the domain is confined to entity names and sample data and can
  be swapped in an hour if the owner prefers another option.

## Confirmation

Accepted by the owner on 2026-09-24: three small services with a network/infrastructure
domain (`site-service`, `circuit-service`, `notification-service`).
