# Phase 5: outbound team webhooks (operator opt-in)

This release sends **deployment timeline events**, including approval requests,
approvals, rejections and terminal deployment states, to one public HTTPS endpoint per
team. Team owners and admins can manage a database-backed endpoint through the API. A
project inherits only its own team's endpoint; project and API-key credentials cannot
configure destinations.

Database-backed settings require the same encryption key ring in both API and worker.
Supply it through a secret manager or untracked local `.env` file, never source control:
`WEBHOOK_SECRETS_ENABLED=true`, `WEBHOOK_SECRETS_CURRENT_KEY_ID=key1`, and
`WEBHOOK_SECRETS_KEY_1=<base64-encoded-32-byte-key>`. Compose passes these values to both
containers. The feature is disabled by default. When enabled, the API accepts
`PUT /api/v1/teams/{slug}/webhook` with JSON `{ "url": "https://…", "secretBase64": "…" }`;
the URL must be HTTPS on port 443 and the base64 secret must decode to 32–1024 bytes.
`GET /api/v1/teams/{slug}/webhook` returns only the URL, configured flag, update time
and selected event types; it never returns the signing secret or ciphertext. `PUT` may
include `eventTypes`, a non-empty array of deployment event names; omitting it subscribes
to all current event types for backwards compatibility. Only an active team owner or admin
can use these endpoints. `GET /api/v1/teams/{slug}/webhook/deliveries` returns up to 100
recent team deployment events with delivery status and durable per-attempt history. A stored database setting takes precedence over legacy worker
configuration. For teams without a database setting, the worker continues to support
`Webhooks__Teams__<team-guid>__Url` and `Webhooks__Teams__<team-guid>__Secret` for
backwards compatibility.

Secrets are encrypted with AES-256-GCM, a random nonce and authentication tag; the team
ID is authenticated as associated data to prevent moving ciphertext between teams. The
database stores ciphertext only. The encryption keys are separate from the HMAC signing
secrets. Never log or commit either kind of key. Existing events are not replayed automatically
when a webhook is enabled; events created while a team has no configured URL are acknowledged
without delivery. An owner or admin can replay a stored event manually (see "Historical replay").

Encryption key rotation uses two configured slots (`key1` and `key2`). Add a new
32-byte base64 key in the unused slot and set `WEBHOOK_SECRETS_CURRENT_KEY_ID` to that
slot, keeping both key values available to API and worker. After deploying the same
configuration to both, an admin calls `POST /api/v1/admin/webhooks/rewrap`. The operation
rewrites stored ciphertext under the active key and returns the number rewrapped. Keep
the previous key configured until rewrap succeeds; then it can be removed. Never remove
a key while any database row still references it. Rotate the webhook signing secret
itself by sending a replacement `secretBase64` to the team PUT endpoint; the response
does not reveal it.

The POST body is JSON with `id`, `eventType`, `occurredAt`, `deploymentId`,
`projectId`, `environmentId`, `artifactId`, `sequence`. The event type and sequence
describe the **event at occurrence time**, not the deployment's current state when
delivery eventually occurs. The `X-AegisOps-Event-Id` header is stable across retries;
deduplicate by that ID. `X-AegisOps-Timestamp` is Unix seconds and
`X-AegisOps-Signature` is `sha256=<lowercase hex HMAC-SHA256>` over the UTF-8 bytes of
`<timestamp>.<raw request body>` using the decoded secret. Verify the timestamp is
recent, check the signature using a constant-time comparison, and reject replays.

Each event and its `SendNotification` job commit atomically in PostgreSQL. The worker
uses the existing 5-attempt exponential-backoff/dead-letter queue. Inspect failed jobs
via the admin jobs API; error text is intentionally generic and contains no destination,
secret, response body or HTTP exception details. Retrying or lease recovery can send a
duplicate, so receiver-side idempotency is required. Network access uses direct HTTPS
with normal certificate/hostname verification, redirects and proxies disabled; every
DNS answer is checked for public IPv4 and the vetted IP is pinned for the connection.
IPv6, non-443 ports and local/private/special-purpose addresses are unsupported.

## All-time delivery summary and telemetry

`GET /api/v1/teams/{slug}/webhook/summary` returns all-time per-team counters:
`{ delivered, failedAttempts, deadLettered, lastSuccessAt, lastFailureAt }`. Authorization is the
same as for `deliveries` (`teams:write`, then Admin or an Owner of the team; members and
non-members get `404`). The counters live in `org.team_webhook_delivery_stats` (migration
`TeamWebhookDeliveryStats`), one row per team, referencing only the team. The Worker increments
them with one atomic `INSERT … ON CONFLICT DO UPDATE` inside the same transaction that commits the
attempt row and job result, so concurrent workers cannot lose increments, and a rolled-back
completion counts nothing. Because the rows do not reference jobs, `JobCleanupService` purging
`Succeeded` jobs after seven days does not change them. `delivered` counts 2xx responses;
`failedAttempts` counts every failed attempt, including retries, lease expiry and the final attempt
of a dead-lettered job; `deadLettered` counts jobs that exhausted their five attempts. A
notification that was skipped (no destination, event type not subscribed, event deleted) is not
counted. Counting starts when this migration is applied; earlier history is not backfilled.

The Worker also exports, on `Meter("AegisOps")`, `aegisops_webhook_deliveries_total{outcome}` with
`outcome` = `Succeeded`, `Failed` (retry scheduled) or `DeadLettered` (final attempt failed), one
increment per committed attempt, and `aegisops_webhook_delivery_duration_seconds{outcome}` (HTTP
attempt time; buckets 0.05–30 s). No team, URL, event, deployment or job identifiers are labels.
Counter series are seeded with zero at Worker start-up; the histogram is not seeded.

## Historical replay

`POST /api/v1/teams/{slug}/webhook/deliveries/{eventId}/replay` (same authorization as above)
re-sends a stored deployment event of that team. It enqueues a new `SendNotification` job with the
same payload as the outbox, so the Worker delivers it through the normal sender: current URL,
current subscriptions and secret, SSRF/IP checks, fresh timestamp and signature, and the usual
retry and dead-letter handling. The `X-AegisOps-Event-Id` stays the original event id, so receivers
that deduplicate by it will treat the replay as a duplicate. It works for events older than job
retention as long as the event row exists. At most one `Pending` or `Running` notification job may
exist per event: otherwise the request gets `409` (PostgreSQL serializes concurrent replays of the
same event with a transaction-scoped advisory lock). Success returns `202` with `{ eventId, jobId }`
and writes the audit event `webhook.replayed` (resource `DeploymentEvent`, the event id). Events of
other teams and unknown ids return `404`. If the team has no destination or does not subscribe to
the event type, the replay job completes without sending.

**Implemented:** team-owner management UI, selective event subscriptions, recent delivery
history with durable per-attempt status, timestamps and generic failure text. Each webhook
job claim and attempt row are committed atomically; attempt completion is committed with
the job result, and stale lease recovery marks the attempt failed. Failed-attempt details
never include the destination, secret, response body, or transport exception. The team
panel summarizes delivered, dead-lettered, queued, not-queued events and attempt counts
across its 100 most recent deployment events; these are recent-window metrics. All-time counters,
Prometheus delivery metrics and historical replay are described above.

Automated tests cover signed delivery, non-2xx handling, delivery-attempt history, and
failure redaction against a local TLS receiver. They do not verify public DNS resolution,
IP pinning, or the production certificate-validation path.

Telemetry and replay are covered by `TeamWebhookTelemetryTests` (SQLite: counters across the
job purge, team isolation, replay authorization, duplicate guard, audit row, replay after purge;
metric labels via `MeterListener`; migration/snapshot consistency). The end-to-end processor test
on PostgreSQL runs only when `AEGISOPS_TEST_POSTGRES` is set. The team panel UI does not yet show
the summary or a replay button.

**Remaining work:** integration testing against an externally hosted HTTPS receiver, and UI for
the all-time summary and replay. This implementation does not
provide exactly-once delivery or a reliable receiver acknowledgement beyond a 2xx response.
