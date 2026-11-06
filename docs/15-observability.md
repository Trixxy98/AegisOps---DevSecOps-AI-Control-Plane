# 15 — Observability

Phase 7 deliverable; health checks and structured logging exist from Phase 1.

```
Api / Worker ──► OpenTelemetry SDK ──► /metrics (Prometheus exposition) ──► Prometheus ──► Grafana
      │
      └──► Serilog (JSON console) ──► docker logs (Loki is V2)
      └──► Traces → console exporter (dev) / OTLP (optional collector)
```

## 1. Logging (Serilog)

| Aspect | Decision |
| --- | --- |
| Sinks | Console; compact JSON (`Serilog.Formatting.Compact`) when `DOTNET_RUNNING_IN_CONTAINER` |
| Enrichers | `CorrelationId`, `UserId`/`ApiKeyId`, `Module`, `MachineName`, `TraceId`/`SpanId` |
| Request logging | Serilog `UseSerilogRequestLogging` with path, status, elapsed, principal type; health/metrics excluded |
| Levels | `Information` default; `Microsoft.AspNetCore` = `Warning`; `AegisOps.Policies` = `Debug` in Development |
| PII | Never log prompt content, tokens, passwords, API keys; `Destructure.ByTransforming` for DTOs with sensitive fields |
| Event naming | Message templates with stable property names: `Deployment {DeploymentId} transitioned {From} -> {To}` |

## 2. Metrics (OpenTelemetry → Prometheus)

Instrumentation: `OpenTelemetry.Instrumentation.AspNetCore`, `.Http`, `.Runtime`,
`Npgsql` (built-in tracing/metrics), custom `Meter("AegisOps")`.

First metrics slice: the Worker exports `/metrics` on its internal port and records
`aegisops_jobs_processed_total` and `aegisops_jobs_duration_seconds` after each
committed job attempt. `status` reflects the persisted state (`Succeeded`, `Pending`
for a retry, or `DeadLettered`); cancelled attempts that lose their lease are not
counted as processed. A separate `aegisops_jobs_lease_expired_total` counter records
stale-lease recovery after commit, including interrupted executions that become
`DeadLettered`. Duration buckets are configured in seconds. The Prometheus ASP.NET
Core exporter is pinned at `1.20.1-beta.1` alongside OpenTelemetry `1.20.1` because
its package is prerelease; revisit the exporter before production use.
In the default Compose stack the Worker port is not published to the host, but any
network peer can scrape it. API metrics, the remaining instruments, Prometheus,
Grafana, and alerting below are still design targets, not implemented features.

Worker queue-depth sampling polls PostgreSQL every 15 seconds. `Pending` counts
jobs due now (`ScheduledAt <= now`); `Delayed` counts Pending jobs waiting for
retry/back-off. `Running` and `DeadLettered` are separate series. All four
statuses are emitted, including zeroes. A failed poll clears the snapshot until
sampling succeeds again. One grouped query excludes historical `Succeeded` jobs
before aggregation, so each sample is internally consistent and the
`(status, scheduled_at)` index can be used.

Each Worker samples the same shared jobs table: dashboard/alert queries must
scope to one AegisOps deployment and use `max by (status) (aegisops_jobs_queue_depth)`
across Worker replicas, never `sum` across them.
Dead-lettered jobs accumulate until retention/cleanup is implemented; alert on
new dead-letter events via the counters below, not on the absolute gauge value.

| Metric | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `aegisops_deployments_total` | counter | `project`, `environment_tier`, `status` (terminal) | Deployment outcomes |
| `aegisops_deployment_duration_seconds` | histogram | `environment_tier`, `executor` | `Deploying → Succeeded/Failed` |
| `aegisops_deployment_lead_time_seconds` | histogram | `environment_tier` | `Requested → Succeeded` (includes approval wait) |
| `aegisops_approval_wait_seconds` | histogram | `environment_tier` | `AwaitingApproval → Approved/Rejected` |
| `aegisops_policy_evaluations_total` | counter | `decision` | Allow / RequireApproval / Deny |
| `aegisops_policy_rule_failures_total` | counter | `rule_type`, `effect` | Which rules block most |
| `aegisops_scan_findings` | gauge | `project`, `scanner`, `severity` | Open findings on the latest artifact |
| `aegisops_scan_ingest_duration_seconds` | histogram | `scanner`, `format` | Parsing time |
| `aegisops_ai_requests_total` | counter | `model`, `status`, `reason` | Completed / Blocked(reason) / Failed |
| `aegisops_ai_request_latency_seconds` | histogram | `model` | Ollama round-trip |
| `aegisops_ai_tokens_total` | counter | `model`, `kind` (`prompt`/`completion`) | Usage |
| `aegisops_ai_sensitive_detections_total` | counter | `type`, `action` | Governance value in numbers |
| `aegisops_jobs_processed_total` | counter | `type`, `status` | Worker throughput; committed job attempts |
| `aegisops_jobs_lease_expired_total` | counter | `type`, `status` | Stale lease recoveries, including dead letters |
| `aegisops_jobs_duration_seconds` | histogram | `type` | Job attempt time, including completion commit |
| `aegisops_jobs_queue_depth` | gauge | `status` (`Pending` due, `Delayed`, `Running`, `DeadLettered`) | Worker queue snapshot (poll every 15 s); use max across replicas |
| `aegisops_realtime_connections` | gauge | — | SignalR connected clients |
| `aegisops_audit_events_total` | counter | `outcome` | Incl. `Denied` attempts |
| standard | — | — | `http_server_request_duration_seconds`, `process_*`, `dotnet_*` |

Cardinality guard: `project` label limited to project **slug** (tens of values); never
user ids, deployment ids or versions as labels.

## 3. Tracing

- `ActivitySource("AegisOps")`; spans for use-case handlers (`RequestDeployment`,
  `EvaluateDeployment`), policy evaluation (`PolicyEngine.Evaluate` with `decision`
  attribute), scanner parsing, Ollama calls (`gen_ai.request.model`, token counts per
  semantic conventions), Docker executor steps.
- Correlation: incoming `X-Correlation-Id` stored as span attribute and baggage; job
  payloads carry `correlationId` so Worker spans link to the originating request
  (`ActivityLink`).
- Export: console in Development; `OTLP` if `OTEL_EXPORTER_OTLP_ENDPOINT` set (Jaeger /
  Grafana Tempo optional, not in the default compose).

## 4. Health checks

| Endpoint | Checks | Used by |
| --- | --- | --- |
| `/health/live` | process up | Docker `HEALTHCHECK` |
| API `/health/ready` | PostgreSQL connectivity, pending migrations, Redis `PING`; Ollama reported as **Degraded** (not failing) | Compose monitoring, UI status facade |
| Worker `/health/ready` | PostgreSQL, Redis, migrations, Docker as Degraded, and last successful poll within 30 seconds | API status facade, internal monitoring |

The authenticated `GET /api/v1/system/dependencies` endpoint combines the API's Ollama check with the Worker's `/health/ready` response. The API reaches the Worker at `http://worker:8081/` in Compose; set `Worker__HealthBaseUrl` when running the Worker at a different address. The signed-in UI refreshes this status every 30 seconds and on window focus, shows Ollama/Docker/Worker warnings without blocking navigation, and reports the Worker as unreachable rather than guessing Docker's state when its health endpoint cannot be read.

## 5. Grafana dashboards (provisioned)

| Dashboard | Panels |
| --- | --- |
| **AegisOps Overview** | Deployments/day by outcome · pending approvals · policy decisions pie · p95 API latency · error rate · jobs queue depth |
| **Delivery** | Lead time & approval wait histograms per tier · deployment duration · rule failures top-10 · findings by severity per project |
| **AI Governance** | Requests by status/reason · sensitive detections by type · latency per model · tokens/day · top users (by count) |
| **Runtime** | .NET GC/heap/threads · HTTP throughput · Npgsql pool · Redis latency · SignalR connections |

Dashboards are JSON files in `deploy/grafana/dashboards/`, provisioned with
`deploy/grafana/provisioning/`. Prometheus scrape config targets `api:8080/metrics` and
`worker:8081/metrics` every 15 s.

## 6. Alerting (lightweight)

Prometheus rules in `deploy/prometheus/rules.yml` (Grafana Unified Alerting displays them):

| Alert | Condition |
| --- | --- |
| `JobsDeadLettered` | `(sum(increase(aegisops_jobs_processed_total{status="DeadLettered"}[15m])) or vector(0)) + (sum(increase(aegisops_jobs_lease_expired_total{status="DeadLettered"}[15m])) or vector(0)) > 0` |
| `WorkerStalled` | `max by (status) (aegisops_jobs_queue_depth{status="Pending"}) > 0` for 10 m and no processed jobs (scope to one deployment) |
| `HighApiErrorRate` | 5xx ratio > 5% over 5 m |
| `AiBlockedSpike` | blocked AI requests > 20 in 5 m |
| `OllamaDown` | readiness degraded for 5 m |

Notification channel: none by default (dashboards only); a webhook can be configured.

## 7. What "good" looks like in a demo

1. Trigger a few deployments (including one denial and one approval).
2. Open **Delivery** dashboard: decisions pie shows Allow/RequireApproval/Deny; rule
   failures show `MaxFindings` as the top blocker.
3. Send an AI prompt with a fake AWS key: **AI Governance** shows `AWS_ACCESS_KEY` /
   `Redact` incrementing live.
