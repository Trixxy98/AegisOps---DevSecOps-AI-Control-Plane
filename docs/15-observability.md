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
| `aegisops_jobs_processed_total` | counter | `type`, `status` | Worker throughput |
| `aegisops_jobs_duration_seconds` | histogram | `type` | Handler time |
| `aegisops_jobs_queue_depth` | gauge | `status` (`Pending`, `Running`, `DeadLettered`) | Backlog (polled every 15 s) |
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
| `/health/ready` | PostgreSQL (`SELECT 1`), Redis `PING`, migrations applied; Ollama and Docker socket reported as **Degraded** (not failing) | Compose `depends_on: condition: service_healthy`, UI banner |
| Worker `/health` | same + "last successful job poll < 30 s ago" | Prometheus up/down |

`AspNetCore.HealthChecks.*` packages for Npgsql/Redis; custom checks for Ollama and Docker.

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
| `JobsDeadLettered` | `increase(aegisops_jobs_processed_total{status="DeadLettered"}[15m]) > 0` |
| `WorkerStalled` | `aegisops_jobs_queue_depth{status="Pending"} > 0` for 10 m and no processed jobs |
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
