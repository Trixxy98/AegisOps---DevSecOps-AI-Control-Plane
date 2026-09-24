# ADR-0005 — Durable job queue in PostgreSQL instead of a message broker

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Deployments trigger asynchronous work: policy evaluation, optional scanning, container
execution, notifications. The Api and Worker are separate processes, so an in-memory queue
is not an option. Job volume is small (tens to hundreds per day) but jobs must survive
restarts and must not be lost between "deployment created" and "job enqueued".

## Decision

Implement a **durable job table in PostgreSQL** (`jobs.jobs`) consumed by
`BackgroundService`s in the Worker:

- Enqueue in the **same transaction** as the business change (outbox-lite).
- Claim with `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING *`.
- Retry with exponential backoff, `max_attempts`, dead-letter status, stale-lock reaper.
- Per-deployment serialization via a Redis lock.

## Alternatives considered

| Alternative | Why not (now) |
| --- | --- |
| RabbitMQ / MassTransit | Adds a broker to run and learn; at-least-once delivery + outbox still needed to avoid losing the "enqueue" step; overkill for this volume |
| Kafka | Designed for streams and high throughput; heavyweight for a laptop demo |
| Hangfire / Quartz.NET | Solid, but hides the mechanics this project wants to demonstrate; Hangfire's dashboard/storage add surface area |
| In-process `Channel<T>` | Not durable, not cross-process |

## Consequences

- Positive: zero extra infrastructure; transactional enqueue; the mechanism is ~200 lines
  and fully testable with Testcontainers; a strong interview topic (`SKIP LOCKED`,
  idempotent handlers, outbox).
- Negative: polling latency (~1 s) and no fan-out to multiple consumer types; if those
  become needed, MassTransit over RabbitMQ can replace `IJobQueue` behind the same
  interface — this ADR would then be superseded.
