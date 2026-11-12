# Security review — v1 (P8-01)

Review of [17 Security hardening](17-security-hardening.md) against the code on `dev`, 2026-11-11.
Every control row in docs/17 §2–§5 and §7 was checked for concrete evidence in code or configuration,
the material findings were re-checked by hand, and the highest-impact ones were fixed in the same change.
Scanners: the AegisOps `CI` workflow (Backend, Frontend, Security: Gitleaks, Semgrep, Trivy) is green on
`main`, and the four target-repository workflows ran Gitleaks, Semgrep and Trivy with verified SARIF.

## Fixed in this review

| Finding | Impact | Fix | Test |
| --- | --- | --- | --- |
| Docker target `Network` and container name were free text | A team Owner could attach a deployed image to the control-plane network `aegisops` and reach PostgreSQL, Redis (no auth) and Ollama | `DeploymentTarget.Docker` only accepts `aegisops-<name>` networks, names matching `^[a-z0-9][a-z0-9-]{2,62}$`, host ports ≥ 1024 and absolute health paths; the executor re-checks the network before pulling or creating anything (targets stored earlier) | `DeploymentTargetTests` |
| Lockout configured but not enforced | `Login` used `CheckPasswordAsync`, which neither checks nor counts lockout, so the 5-failures / 15-minute rule never fired | Login checks `IsLockedOutAsync`, counts failures with `AccessFailedAsync`, resets on success; a locked account gets the same generic 401 and its password is not checked | `IdentityFlowTests.FiveFailedLoginsLock…` |
| Raw prompt stored when `StoreContent` is on | `prompt_content` held the pre-redaction prompt, contrary to docs/08 ("redacted previews") | `prompt_content` stores the post-inspection prompt | `AiGatewayTests.Stored_prompt_content_is_the_redacted_prompt…` |
| PostgreSQL, Redis (no password), API and web published on all interfaces | Anyone on the LAN could reach the database and Redis | Compose binds 5432, 6379, 8080 and 3000 to `127.0.0.1` | `docker compose config` |

## Verified controls (summary)

Implemented with evidence: project-scoped, expiring, revocable, SHA-256-hashed API keys with a constant-time
compare and no Production by default; rotating, hashed refresh tokens with family revocation and an
`HttpOnly; Secure; SameSite=Strict` cookie; 15-minute HS256 access tokens with issuer, audience and lifetime
validation; the permission × role matrix and the anonymous allow-list enforced by `AuthorizationMatrixTests`
against the real `Program.cs` endpoint table; requester ≠ approver and policy roles frozen at evaluation;
team scoping with 404 for invisible resources; 20 MB scan uploads with server-generated paths; idempotency
keys; parameterized raw SQL only (job claiming, completion, leases, webhook counters); SPA security headers and
CSP without inline scripts; no CORS (same origin through nginx); Serilog PII masking; AI content off by default
with retention; only the Worker mounts the Docker socket, with an ownership label check before replacing a
container; target changes limited to team Owner/Admin and audited; Dependabot for NuGet, npm, Docker and Actions;
complete audit coverage enforced by `AuditCoverageTests`.

## Open items (v1 known gaps, tracked in docs/17 §8)

Ordered by impact. None is exploitable from outside the host now that Compose binds to loopback.

1. **Audit tamper protection is not role-based yet.** The API and Worker share one owner role; no
   `INSERT`/`SELECT`-only role on `audit.*`. *Resolved in v1.1 (roadmap item 6) for the production profile: the
   api and worker connect as `aegisops_app`, created by `migrate` with only `SELECT`/`INSERT` on `audit.*` (also for
   future tables); only `migrate` uses the owner. Nothing in the code updated or deleted audit rows. The Development
   stack still connects as the owner; integrity remains role-based, not cryptographic (docs/17 §2–3).*
2. **No per-principal or upload rate limiting.** Only `/auth/login` and `/auth/refresh` (10/min per client IP)
   and the Redis AI limits exist. Behind nginx all browser users share one IP bucket (no forwarded-headers
   handling); the auth limiter sends no `Retry-After`. *v1.1: forwarded headers from known proxies are honoured
   behind the TLS proxy, so the auth limiter sees real client IPs. Resolved in v1.1 (roadmap item 4): a global token
   bucket per user/API key (client IP when anonymous) on `/api` and `/hubs`, an `uploads` policy for scan uploads,
   and every 429 is Problem Details with `Retry-After` (docs/17 §3). Limits are in memory per API instance.*
3. **Errors:** no `AddProblemDetails`/`UseExceptionHandler`; Compose runs the API as `Development`, so the
   developer exception page is served (now loopback-only). *Resolved in v1.1: unhandled exceptions return Problem
   Details with a `traceId` in every environment, and the production profile runs as `Production`.*
4. **Regex hardening:** policy branch globs have no `NonBacktracking`/timeout; AI `BlockedPatterns` use a
   100 ms timeout but backtracking. *Resolved in v1.1 (roadmap item 7): globs and blocked patterns are compiled with
   `RegexOptions.NonBacktracking` and a 100 ms timeout and validated when the policy is saved (400 Problem Details for
   invalid patterns or unsupported constructs such as backreferences and lookarounds); evaluation fails closed
   (docs/17 §3).*
5. **CI scope:** Semgrep has no `p/owasp-top-ten` and never fails the build; Trivy scans the file system only
   (no image scan of AegisOps images); no `npm audit --audit-level=high`; NuGet audit relies on the SDK's
   restore-time audit. *Resolved in v1.1 (roadmap item 10): Semgrep adds `p/owasp-top-ten` and fails on
   ERROR-severity findings (SARIF still uploaded); `npm audit --audit-level=high --omit=dev`; an explicit
   `dotnet list package --vulnerable --include-transitive` gate; release images are Trivy-scanned (amd64) before
   multi-arch amd64/arm64 images are pushed (docs/17 §7). The first CI run with the new gates and the first
   multi-arch release are still to be observed.*
6. **Repudiation details:** approvals and audit rows carry no client IP; policy changes are audited without a
   before/after diff; the "policy changed N minutes ago" banner is not built.
7. **Stored AI content is not encrypted at rest** (it is now redacted, off by default and deleted after 30 days).
   *Resolved in v1.1 (roadmap item 7): content is stored only AES-256-GCM encrypted with the `AiContent` key ring,
   bound to the request ID and column; without a key nothing is stored (`content_skipped`). Rows written as plaintext
   before the upgrade are not re-encrypted; they expire with retention (docs/17 §5, operations.md §5.6).*
8. **Doc drift corrected in docs/17:** validation is hand-written (no FluentValidation); deny-by-default is
   enforced by the route-table test rather than a fallback policy; webhook secrets use an AES-256-GCM key ring,
   not ASP.NET Data Protection.
