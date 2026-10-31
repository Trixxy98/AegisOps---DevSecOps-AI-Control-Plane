# Phase 5: outbound team webhooks (operator opt-in)

This initial release sends **deployment timeline events**, including approval requests,
approvals, rejections and terminal deployment states, to one operator-configured HTTPS
endpoint per team. No webhook management API is exposed. A project inherits only its
own team's endpoint; no project or API key can set a destination.

Configure **only the worker** with `Webhooks__Teams__<team-guid>__Url` (a public HTTPS
URL on port 443) and `Webhooks__Teams__<team-guid>__Secret` (base64-encoded 32+ random
bytes). Set both *before* starting the worker and before generating events. For Compose,
add them to the worker environment using a secure secret source; do not commit keys or
paste them into logs. Restart/recreate **only the worker** after updating configuration;
do not remove database volumes. Existing events are not replayed on enablement. Jobs
created while a team has no URL are acknowledged without delivery.

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

**Remaining work:** authenticated team-owner management UI/API, secret encryption and
rotation, selective event subscriptions, delivery history/metrics, integration testing
against an externally hosted HTTPS receiver, and historical replay. Automated tests now
exercise the signed request and non-2xx handling against a local TLS receiver; they do
not verify public DNS resolution, IP pinning, or the production certificate-validation
path. This implementation does not attempt to provide exactly-once delivery or a reliable
receiver acknowledgement beyond a 2xx response.
