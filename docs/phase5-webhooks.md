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
`GET /api/v1/teams/{slug}/webhook` returns only the URL, configured flag and update time;
it never returns the signing secret or ciphertext. Only an active team owner or admin
can use these endpoints. A stored database setting takes precedence over legacy worker
configuration. For teams without a database setting, the worker continues to support
`Webhooks__Teams__<team-guid>__Url` and `Webhooks__Teams__<team-guid>__Secret` for
backwards compatibility.

Secrets are encrypted with AES-256-GCM, a random nonce and authentication tag; the team
ID is authenticated as associated data to prevent moving ciphertext between teams. The
database stores ciphertext only. The encryption keys are separate from the HMAC signing
secrets. Never log or commit either kind of key. Existing events are not replayed when a
webhook is enabled; events created while a team has no configured URL are acknowledged
without delivery.

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

**Remaining work:** team-owner management UI, selective event subscriptions, delivery
history/metrics, integration testing against an externally hosted HTTPS receiver, and
historical replay. Automated tests exercise signed delivery and non-2xx handling against
a local TLS receiver; they do not verify public DNS resolution, IP pinning, or the
production certificate-validation path. This implementation does not provide
exactly-once delivery or a reliable receiver acknowledgement beyond a 2xx response.
