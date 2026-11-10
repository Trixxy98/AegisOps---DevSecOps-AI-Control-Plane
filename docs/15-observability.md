# 15 — Observability

Phase 7 deliverable; health checks exist from Phase 1, and Serilog structured logging is configured for the Api and Worker.

```
Api / Worker ──► OpenTelemetry SDK ──► /metrics (Prometheus exposition) ──► Prometheus ──► Grafana
      │
      └──► Serilog (JSON console) ──► docker logs (Loki is V2)
      └──► Traces → console exporter (dev) / OTLP (optional collector)
```

## 1. Logging (Serilog)

| Aspect | Decision |
| --- | --- |
| Setup | `AegisOpsLogging.AddAegisOpsSerilog` (`src/AegisOps.Infrastructure/Observability/AegisOpsLogging.cs`), shared by Api and Worker; `builder.Services.AddSerilog(..., preserveStaticLogger: true)`, so the static `Log.Logger` is never touched and parallel test hosts keep separate loggers. Existing `ILogger<T>` and `[LoggerMessage]` call sites log through Serilog unchanged. |
| Sinks | Console only: compact JSON (`Serilog.Formatting.Compact`, CLEF) in every environment except Development, which uses a readable single-line template |
| Enrichers | `ServiceName` (`aegisops-api` / `aegisops-worker`), `Environment`, `MachineName`, `CorrelationId` (Api: the validated `correlationId` HttpContext item set by request tracing; Worker: the `correlationId` Activity baggage of job spans), `TraceId`/`SpanId` (also `@tr`/`@sp`), plus log-context and `SourceContext` |
| Request logging | `UseSerilogRequestLogging` (`src/AegisOps.Api/Observability/ApiRequestLogging.cs`), after request tracing: one event per request with `RequestMethod`, `RouteTemplate` (`(unmatched)` when no endpoint matched), `StatusCode`, `Elapsed` ms, `CorrelationId`, `PrincipalType` and an opaque `UserId`/`ApiKeyId`. `/health*` and `/metrics` are logged at `Verbose` (dropped at the default level); 5xx/exceptions at `Error` |
| Levels | Code defaults `Information`; `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `System.Net.Http.HttpClient` = `Warning`; refined by the `Serilog:MinimumLevel` section of `appsettings*.json` (`AegisOps.Policies` = `Debug` in Development) |
| PII | See "Logging and PII" below; secrets never logged (docs/17 §3) |
| Event naming | Message templates with stable property names: `Deployment {DeploymentId} transitioned {From} -> {To}` |

### Logging and PII

**Logged:** the request method, route template, status code, elapsed time, CorrelationId, trace/span ids,
principal type and opaque user/API-key GUIDs; application events through their message-template properties;
service, environment and machine name.

**Never logged:** `Authorization` and `Cookie` headers (no header is logged at all), request and response bodies,
query strings, raw request paths (the `RequestPath` that ASP.NET log scopes attach is removed; only route
templates are written), prompts and AI responses, emails, passwords, tokens, API keys and secrets.

**Enforcement:** `SensitivePropertyMasker` is the last enricher in the pipeline. It replaces with `***` the value of
any property, at any depth (destructured objects, dictionaries, sequences), whose name, ignoring case and
separators, contains `password`, `passwd`, `secret`, `token`, `apikey`, `authorization`, `cookie`, `credential`,
`email`, `prompt`, `connectionstring`, `privatekey` or `signingkey`. Compact JSON writes message templates, not
rendered messages, so masked values do not leak through `@mt`. Code must still not put sensitive values into
properties with innocuous names: the masker is a backstop, not a licence.

**Single exception:** in the Development environment only, events from the `DevelopmentUserSeed` source are not
masked, because the randomly generated first-run passwords of the synthetic demo accounts are shown only in that
log line (`docker compose logs api | grep 'Development user'`). The exemption is inert in every other environment.

Verified by `tests/AegisOps.Api.Tests/StructuredLoggingTests.cs`.

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
Dead-lettered jobs are intentionally retained until an Admin retries them (the
Worker's `JobCleanupService` deletes only `Succeeded` jobs older than seven days); alert on
new events via the counters *or a rise from the 15-minute minimum of the
DeadLettered queue gauge*, never on the absolute gauge value. The gauge comparison
catches a first event when a zero queue snapshot was previously scraped, even
if no zero counter sample was scraped before the event. Pre-seeding counters
alone cannot recover an event that occurred before the first Prometheus scrape.

The table includes both implemented instruments (described above) and remaining
design targets; a row alone does not assert that a metric is exported.

| Metric | Type | Labels | Meaning |
| --- | --- | --- | --- |
| `aegisops_deployments_total` | counter | `environment_tier`, `status` (terminal) | **Implemented** (API + Worker, at commit). `project` omitted to keep cardinality bounded |
| `aegisops_deployment_duration_seconds` | histogram | `environment_tier`, `status` | **Implemented**: `Deploying → Succeeded/Failed` |
| `aegisops_deployment_lead_time_seconds` | histogram | `environment_tier` | **Implemented**: `Requested → Succeeded` (includes approval wait) |
| `aegisops_approval_wait_seconds` | histogram | `environment_tier` | `AwaitingApproval → Approved/Rejected` |
| `aegisops_policy_evaluations_total` | counter | `decision` | **Implemented** for Worker evaluations: Allow / RequireApproval / Deny |
| `aegisops_policy_rule_failures_total` | counter | `rule_type`, `effect` | Which rules block most |
| `aegisops_scan_findings` | gauge | `project`, `scanner`, `severity` | Open findings on the latest artifact |
| `aegisops_scan_ingest_duration_seconds` | histogram | `scanner`, `mode` (`Inline`/`Queued`) | **Implemented**: SARIF parse + normalisation time |
| `aegisops_scans_total` | counter | `scanner`, `status` (`Completed`/`Failed`) | **Implemented**: committed scan outcomes |
| `aegisops_ai_requests_total` | counter | `status`, `reason` (bounded) | Committed gateway request outcomes |
| `aegisops_ai_request_latency_seconds` | histogram | `status` | **Implemented**: gateway time per committed request; no `model` label (model names can be user-defined) |
| `aegisops_ai_tokens_total` | counter | `kind` (`prompt`/`completion`) | **Implemented**: provider-reported tokens; no `model` label |
| `aegisops_ai_sensitive_detections_total` | counter | `type`, `policy_action` | Distinct known inbound types per committed request; policy setting, not actual block outcome |
| `aegisops_jobs_processed_total` | counter | `type`, `status` | Worker throughput; committed job attempts |
| `aegisops_jobs_lease_expired_total` | counter | `type`, `status` | Stale lease recoveries, including dead letters |
| `aegisops_jobs_duration_seconds` | histogram | `type` | Job attempt time, including completion commit |
| `aegisops_webhook_deliveries_total` | counter | `outcome` (`Succeeded`/`Failed`/`DeadLettered`) | **Implemented** (Worker, after the attempt commits): one per webhook attempt; `Failed` = retry scheduled, `DeadLettered` = final attempt failed; skipped notifications are not counted. Zero-seeded. No team, URL or event labels |
| `aegisops_webhook_delivery_duration_seconds` | histogram | `outcome` | **Implemented**: outbound HTTP attempt time (buckets 0.05–30 s); none for lease expiry. Durable per-team totals: `GET /teams/{slug}/webhook/summary` (docs/phase5-webhooks.md) |
| `aegisops_jobs_queue_depth` | gauge | `status` (`Pending` due, `Delayed`, `Running`, `DeadLettered`) | Worker queue snapshot (poll every 15 s); use max across replicas |
| `aegisops_realtime_connections` | gauge | — | SignalR connected clients |
| `aegisops_audit_events_total` | counter | `outcome` (known set, else `Other`) | **Implemented** (at commit). Incl. `Denied` attempts |
| standard | — | — | `http_server_request_duration_seconds`, `process_*`, `dotnet_*` |

Cardinality guard: `project` label limited to project **slug** (tens of values); never
user ids, deployment ids or versions as labels.

## 3. Tracing

The API and Worker now register `ActivitySource("AegisOps")` with OpenTelemetry.
Custom spans cover API deployment requests, evaluation/execution jobs, policy
engine decisions (`decision` is bounded), SARIF parsing (`scanner.type`), Ollama
chat (nonnegative token counts), and Docker executor steps (fixed names and
bounded outcome only). The model name is deliberately **not** exported: it can
be user-defined, so the earlier target of `gen_ai.request.model` remains a
privacy-gated design item. Prompts, responses, scan contents, image references,
container names, URLs, and exception messages are not set as trace attributes.

For API requests, a single `X-Correlation-Id` with 1–64 ASCII letters, digits,
`-`, or `_` is accepted; otherwise an opaque identifier is generated. The
result becomes a span attribute and baggage entry for the request. Each
Deployment instead receives a server-generated correlation ID for its audit,
job, and payload records, so clients cannot reuse another deployment's audit
correlation. Deployment job payloads persist that server-generated ID plus W3C
`traceparent`; queued scan payloads carry `traceparent`. The Worker creates a
consumer span and links valid persisted context via `ActivityLink`;
legacy/malformed payloads omit the link without changing their existing job
failure path. SQLite tests cover persisted
API-request → job-payload → Worker-link behavior. On 2026-11-09, a single
approved Development noop deployment succeeded in rebuilt local API/Worker
containers. Console-exported Worker `EvaluateDeployment` linked to the API
`RequestDeployment` span, and the Worker `ExecuteDeployment` job linked to its
preceding evaluation span; both persisted jobs succeeded.

In a separate approved read-only probe on 2026-11-09, a temporary OTLP/HTTP
receiver accepted two protobuf trace batches from rebuilt local containers.
Unauthenticated API GETs (404 and 401) produced `Api.Request` spans with the
`aegisops-api` service resource in both batches. No job or deployment was
submitted for this probe, so Worker OTLP export was not observed. The temporary
receiver was stopped and the original API/Worker Compose environment restored;
40 deployments and zero active jobs remained afterward.

A separately approved isolated Docker probe used an in-memory artifact/target,
not a Staging environment or queued job. The real Docker executor pulled a
public test image, created and started a dedicated temporary container, passed
its health check, and returned success. A local `ActivityListener` observed
`Docker.PullImage`, `Docker.CreateContainer`, `Docker.StartContainer`,
`Docker.HealthCheck`, and `Docker.Execute` (status `Ok`). The probe container and
its host port were cleared afterward; deployment records stayed at 40. This
first run did **not** export Docker spans to OTLP: the scratch harness set the
HTTP endpoint without `/v1/traces`, and the receiver captured no batch. After
configuring that path, a separate synthetic `Probe.OtlpSmoke` span reached the
collector. Do not conflate that smoke batch with the actual Docker spans or
with Worker OTLP export.

The trace provider uses the console exporter in Development and an optional OTLP
exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (a collector such as Jaeger or
Tempo is not in the default Compose stack). An unsampled ASP.NET ambient parent
initially suppressed ordinary custom spans; the provider now starts before
work and uses an always-on sampler for bounded `AegisOps` spans. A read-only
unauthenticated API request then produced a console-exported `Api.Request`
span without sending a sampled `traceparent`. Because ASP.NET's own request
Activity is not exported, `Api.Request` is started as a root span (or as a child
of a valid incoming W3C `traceparent`) rather than as a child of that missing
span, and caller-supplied `baggage` is not inherited. Failed jobs, Docker steps,
`Docker.Execute`, and Ollama calls set span status `Error` without exception
messages or events. An unhealthy Docker health check and failed restore step
also mark their spans `Error`. API and Worker export distinct `service.name`
resource values; the third-party webhook HTTP handler does not propagate trace
headers or correlation baggage. Review always-on sampling volume before
production use. Worker OTLP collector export and Docker executor spans exported
from a real Worker job remain unobserved; real scanner/Ollama traffic is also
not covered by this probe. P7-03 remains Unverified.

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
the first event after a baseline scrape is visible. Signed-in screenshots on
2026-11-08 verified live event data on Overview, Delivery, and AI Governance;
Delivery's p95 attempt-duration panel remained blank with only one observation
of each job type.

The following panels remain *design targets*, not yet instrumented:

| Dashboard | Planned panels |
| --- | --- |
| **Delivery** | Lead time & approval wait histograms per tier · deployment duration · rule failures top-10 · findings by severity per project · policy decisions |
| **AI Governance** | Ollama latency per model · tokens/day · top users (by count; requires a privacy review, not a user-ID metric label) |
| **Runtime** | .NET GC/heap/threads · HTTP throughput · Npgsql pool · Redis latency · SignalR connections |

The dashboard contract validates three unique provisioned JSON dashboards and
implemented series. P7-08 was verified with signed-in live screenshots of all
three dashboards and matching persisted event/Prometheus evidence.

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
