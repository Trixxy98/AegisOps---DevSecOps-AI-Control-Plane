# 15 — Observability

Phase 7 deliverable; health checks exist from Phase 1, while Serilog structured logging remains a design target.

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

Instrumentation targets: built-in ASP.NET Core HTTP server metrics via the
`Microsoft.AspNetCore.Hosting` and `AegisOps.Ai` meters on the API, and the custom
`Meter("AegisOps")` on the Worker. Runtime, Npgsql, tracing, and the remaining
custom instruments below are design targets, not yet exported.

The API exports `http_server_request_duration_seconds` via `/metrics` only on its
internal management listener (port `9464`). The API image listens on `8080` and
`9464` by default; Compose publishes only `8080` to the host. Requests to
`/metrics` on the public API listener return 404, and every other route on
`9464` returns 404. The management listener is not an authentication boundary:
restrict access to trusted network peers. Local runs must bind both ports
explicitly, for example with
`ASPNETCORE_URLS='http://127.0.0.1:8080;http://127.0.0.1:9464'`.
The HTTP histogram uses route templates rather than raw request paths (no user
IDs in metric labels), covers responses including 5xx, and excludes scrape
requests themselves. Grafana p95 and 5xx ratios can derive from its histogram
buckets and count series after Prometheus is provisioned.

The Worker exports `/metrics` on its internal port and records
`aegisops_jobs_processed_total` and `aegisops_jobs_duration_seconds` after each
committed job attempt. `status` reflects the persisted state (`Succeeded`, `Pending`
for a retry, or `DeadLettered`); cancelled attempts that lose their lease are not
counted as processed. A separate `aegisops_jobs_lease_expired_total` counter records
stale-lease recovery after commit, including interrupted executions that become
`DeadLettered`. Duration buckets are configured in seconds. The Prometheus ASP.NET
Core exporter is pinned at `1.20.1-beta.1` alongside OpenTelemetry `1.20.1` because
its package is prerelease; revisit the exporter before production use.
In the default Compose stack the Worker port is not published to the host, but any
network peer can scrape it. Prometheus and Grafana are provisioned in an opt-in
Compose overlay. A prior live Compose run verified both scrape targets, five
alerts, and an Overview screenshot with API p95 and Worker queue-depth data;
its 5xx and processed-job panels had no matching series at that time. The
Delivery and AI Governance dashboards passed file/contract checks. A locally
rebuilt API/Worker stack exported zero-valued counter baselines, and an
authorized local demo produced nonzero Delivery and AI Governance rates in
Prometheus. Signed-in screenshots on 2026-11-08 then showed live Overview API
p95 and processed jobs, Delivery job-attempt rates, and AI Governance request
outcomes and inbound email detection. The Delivery job-attempt-duration p95
panel remains blank after just one observation per job type; this is not a
claim of missing job attempts or a deployment lead-time measurement.

The API records `aegisops_ai_requests_total{status,reason}` only after the AI
request and audit event save succeeds. `reason` is a bounded gateway result (or
`Other`), not a provider exception. It records
`aegisops_ai_sensitive_detections_total{type,policy_action}` once per known
inbound sensitive type per committed request, using the active policy action
or `NoPolicy` when none was available; unknown detector types are excluded.
Neither series has model, user, or prompt labels; cancelled or failed saves
do not emit events. API startup pre-seeds all bounded AI counter labels with
zero values after the meter provider starts. Worker startup does the same for
processed-job and lease-expiry counters before its job processor starts. A
Prometheus zero scrape *before* the first event lets `rate()`/`increase()`
observe that event. Job-duration histograms are not seeded with fake samples;
an idle service can still have no duration series.

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
new events via the counters *or a rise from the 15-minute minimum of the
DeadLettered queue gauge*, never on the absolute gauge value. The gauge comparison
catches a first event when a zero queue snapshot was previously scraped, even
if no zero counter sample was scraped before the event. Pre-seeding counters
alone cannot recover an event that occurred before the first Prometheus scrape.

The table includes both implemented instruments (described above) and remaining
design targets; a row alone does not assert that a metric is exported.

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
| `aegisops_ai_requests_total` | counter | `status`, `reason` (bounded) | Committed gateway request outcomes |
| `aegisops_ai_request_latency_seconds` | histogram | `model` | Ollama round-trip |
| `aegisops_ai_tokens_total` | counter | `model`, `kind` (`prompt`/`completion`) | Usage |
| `aegisops_ai_sensitive_detections_total` | counter | `type`, `policy_action` | Distinct known inbound types per committed request; policy setting, not actual block outcome |
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

## 5. Grafana dashboards (opt-in provisioning)

The opt-in `deploy/docker-compose.observability.yml` overlay provisions Prometheus
(`prom/prometheus:v3.16.0`) and Grafana (`grafana/grafana:13.3.0`). Set
`GRAFANA_ADMIN_PASSWORD` outside version control before enabling it; the default
Compose stack needs no Grafana credentials. Both UIs bind to host loopback only
(Prometheus `:9090`, Grafana `:3001`). Neither API `:9464` nor Worker `:8081` is
published to the host. Prometheus scrapes `api:9464/metrics` and
`worker:8081/metrics` every 15 s over the internal Compose network. Grafana's
Prometheus datasource and **three dashboards** are provisioned from
`deploy/grafana/`. Overview panels use only the implemented API request histogram
and Worker job metrics (API p95, 5xx rate, queue depth, processed jobs). Queue
depth uses `max by (status)` because Worker replicas read the same shared table.
API p95 and 5xx panels exclude `/hubs/` routes: a long-lived WebSocket should not
inflate request latency or trigger the API error-rate alert.

The **Delivery** dashboard currently shows Worker EvaluateDeployment and
ExecuteDeployment *job attempts* by persisted job status and p95 attempt
duration. These are not deployment outcomes, approvals, or end-to-end lead time.
The **AI Governance** dashboard shows committed gateway request rates by
status/reason and inbound sensitive detection rates by type/policy action. The
policy action is a configuration label, not proof that an individual request
was blocked. Both dashboards filter zero-valued rates (`> 0`) to keep idle
combinations out of the legend; their counters remain seeded in Prometheus so
the first event after a baseline scrape is visible. Neither dashboard has yet
been verified with real event data in Grafana.

The following panels remain *design targets*, not yet instrumented:

| Dashboard | Planned panels |
| --- | --- |
| **Delivery** | Lead time & approval wait histograms per tier · deployment duration · rule failures top-10 · findings by severity per project · policy decisions |
| **AI Governance** | Ollama latency per model · tokens/day · top users (by count; requires a privacy review, not a user-ID metric label) |
| **Runtime** | .NET GC/heap/threads · HTTP throughput · Npgsql pool · Redis latency · SignalR connections |

The dashboard contract validates three unique provisioned JSON dashboards and
implemented series. Earlier Compose evidence applies to Overview only. P7-08
remains Missing until all three dashboards show live data during a demo.

## 6. Alerting (lightweight)

Prometheus evaluates the rules in `deploy/prometheus/rules.yml`:

| Alert | Condition |
| --- | --- |
| `ApiMetricsUnavailable` / `WorkerMetricsUnavailable` | Prometheus scrape `up == 0` for 5 m |
| `JobsDeadLettered` | New dead-lettered events from processed/lease-expired counter increases **or** a rise in the shared `DeadLettered` queue gauge above its 15-minute minimum; a constant historical backlog does not fire. |
| `WorkerStalled` | `max by (status) (aegisops_jobs_queue_depth{status="Pending"}) > 0` for 10 m and no processed jobs (scope to one deployment) |
| `HighApiErrorRate` | Non-`/hubs/` 5xx ratio > 5% over 5 m, with more than 20 non-`/hubs/` requests |

`AiBlockedSpike` and `OllamaDown` remain design targets until their metrics exist.
Prometheus evaluates these rules; no Grafana alert rules or notification channel
are provisioned by default. Rule behavior is covered by
`deploy/prometheus/rules.test.yml` (`promtool test rules`).

## 7. What "good" looks like in a demo

1. Trigger a few deployments (including one denial and one approval).
2. Open **Delivery** dashboard: decisions pie shows Allow/RequireApproval/Deny; rule
   failures show `MaxFindings` as the top blocker.
3. Send an AI prompt with a fake AWS key: **AI Governance** shows `AWS_ACCESS_KEY` /
   `Redact` incrementing live.
