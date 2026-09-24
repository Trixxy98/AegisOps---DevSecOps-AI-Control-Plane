# ADR-0004 — Redis only for ephemeral and high-frequency state

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Some operations are high-frequency, short-lived and tolerant of loss: AI rate-limit
counters, idempotency keys, distributed locks, small caches, and a pub/sub channel for
real-time events between the Worker and the Api. Putting these in PostgreSQL would add
write load and awkward cleanup; making Redis a primary store would blur durability
guarantees.

## Decision

Use **Redis 7** (or the API-compatible **Valkey**) strictly for:

| Use | Key pattern | TTL |
| --- | --- | --- |
| AI sliding-window rate limits (Lua script) | `ai:rl:m:{userId}`, `ai:rl:d:{userId}` | 1 min / 1 day |
| Idempotency keys → response snapshot | `idem:{principal}:{key}` | 24 h |
| Per-deployment execution lock | `lock:deployment:{id}` | 10 min |
| Effective policy cache | `cache:policy:{tier}:{projectId}` | 60 s, invalidated on `PolicyChanged` |
| Realtime bridge | channel `aegisops:realtime` | — |

Redis must be **flushable at any time without data loss**. If Redis is down: AI requests
fail closed (429/503), idempotency falls back to natural keys, real-time degrades to
polling, locks fall back to DB row locking.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Everything in PostgreSQL | Rate-limit counters would create hot rows and churn; pub/sub via `LISTEN/NOTIFY` is possible but Npgsql handling adds complexity for little gain |
| In-memory (ASP.NET `IMemoryCache`, `Channel<T>`) | Does not span Api and Worker processes |
| Redis as primary store for jobs/sessions | Durability semantics unclear; PostgreSQL job table is simpler to reason about |

## Consequences

- Positive: clean interview explanation — *"PostgreSQL is my source of truth; Redis holds
  short-lived, high-frequency state I can afford to lose."*
- Negative: one more container; failure modes must be handled explicitly (documented above).
