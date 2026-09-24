# ADR-0003 — PostgreSQL as the single source of truth

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

The data is strongly relational (Team → Project → Environment → Deployment → Evaluation →
Approval; Artifact → Scan → Finding) but also needs flexible documents (rule parameters,
rule results, context snapshots, executor targets). The system needs an audit log, a job
queue and per-module isolation without running several databases.

## Decision

Use **PostgreSQL 17** for all durable state, accessed through **EF Core with Npgsql**:

- One database, **one schema per module**.
- **JSONB** for semi-structured payloads; arrays for tag-like lists.
- `FOR UPDATE SKIP LOCKED` for the job queue.
- Separate DB roles: `aegisops_app` (runtime, no `UPDATE/DELETE` on audit) and
  `aegisops_migrator`.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| MySQL/MariaDB | Weaker JSON indexing and no `SKIP LOCKED` semantics as clean; PostgreSQL is the de-facto standard for this kind of platform |
| SQL Server | Licensing/containers heavier; PostgreSQL keeps the stack 100% free and portable |
| MongoDB | Relational integrity (approvals, evaluations, uniqueness constraints) matters more than schema flexibility |
| Separate DB per module | Cross-module transactions (deployment + job + audit) become distributed; unnecessary at this scale |

## Consequences

- Positive: transactional consistency across modules; audit and jobs need no extra
  infrastructure; JSONB keeps rule parameters flexible while core relations stay typed.
- Negative: JSONB fields are less queryable/validated at the DB level → validated in the
  Application layer; EF migrations for JSON/array columns need review.
