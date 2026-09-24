# 17 — Security Hardening (of AegisOps itself)

A control plane that gates production must be harder to abuse than the things it gates.
This document is the threat model and control list for the platform.

## 1. Assets

| Asset | Why it matters |
| --- | --- |
| Policy definitions | Weakening a policy silently lets bad artifacts into production |
| Approval records & audit log | Evidence; must be tamper-evident |
| API keys & JWT signing key | Impersonating CI or users |
| Docker socket (Worker) | Root-equivalent on the host |
| AI prompts/responses | May contain sensitive data even after redaction |
| Scan reports | Reveal vulnerabilities of target services |

## 2. Threat model (STRIDE-lite)

| Threat | Scenario | Controls |
| --- | --- | --- |
| **Spoofing** | Stolen API key used to register a "clean" artifact | Project-scoped keys, expiry ≤ 1 y, revocation, `LastUsedAt` anomaly visible, keys cannot deploy to Production by default, image digest recorded |
| **Spoofing** | Stolen refresh token | Rotation + family revocation on reuse; httpOnly/SameSite=Strict/Secure cookie; short access tokens |
| **Tampering** | Insider edits audit rows | DB role without `UPDATE/DELETE` on `audit.*`; hash-chain of audit rows (V2) |
| **Tampering** | Policy weakened before a risky deploy | Policy changes audited with diff; policy version frozen into each evaluation; `Security`/`Admin` only; UI shows "policy changed 3 min ago" banner on evaluation panel |
| **Repudiation** | "I never approved that" | Approvals tied to authenticated user id + IP + timestamp; audit event in same transaction |
| **Information disclosure** | Findings/report paths leak to wrong team | Team scoping on every read; 404 instead of 403 for invisible resources; raw reports served only through authorized endpoint |
| **Information disclosure** | Prompts stored with secrets | `StoreContent = false` default; previews are post-redaction; response secret scan |
| **Denial of service** | Flooding scan uploads / AI requests | Body size limits (20 MB), per-principal rate limits, Redis-backed AI limits, job queue back-pressure (`MaxConcurrency`) |
| **Elevation of privilege** | Developer approves own production deploy | Resource-based authorization handler: requester ≠ approver, role in policy roles |
| **Elevation of privilege** | Worker compromised → host takeover via Docker socket | See §4 |
| **Supply chain** | Vulnerable base image or NuGet in AegisOps | Dogfooded Trivy/Gitleaks/Semgrep in CI with Critical fail; Dependabot; pinned actions by SHA (V2) |

## 3. Application controls

| Area | Control |
| --- | --- |
| Transport | TLS terminated by reverse proxy (Caddy/nginx) outside Compose demo; HSTS in production-like; HTTP only on loopback for demo |
| Headers | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, CSP for the SPA (`default-src 'self'`; no inline scripts) via nginx |
| CORS | Explicit allow-list; disabled when web and API share origin (Compose) |
| Auth | Identity password hashing (PBKDF2, Identity defaults, 100k+ iterations), lockout, JWT `HS256` with ≥ 256-bit key (or `RS256` keys if multi-instance later), `nbf/exp/iss/aud` validated, clock skew 30 s |
| Authorization | Deny by default: every endpoint requires authorization unless explicitly `AllowAnonymous` (enforced via fallback policy + architecture test) |
| Input validation | FluentValidation on all commands; max lengths; slug regex `^[a-z0-9-]{3,40}$`; JSON depth limits; enums parsed strictly |
| Output encoding | JSON only; React escapes by default; no `dangerouslySetInnerHTML` |
| Injection | EF Core parameterization; no raw SQL except the job-claim query with parameters; regex patterns from policies compiled with `RegexOptions.NonBacktracking` + timeout to prevent ReDoS |
| File handling | Reports saved under server-generated paths only; original filenames never used; content-type sniffed |
| Secrets | Env vars / user-secrets / Docker secrets; `.env` ignored; Gitleaks on every commit; secrets never logged (Serilog destructuring policy) |
| Errors | Problem Details without stack traces outside Development; `traceId` for support |
| Rate limiting | ASP.NET rate limiter: global per-principal, stricter on `/auth/*` and uploads |
| Dependency hygiene | `dotnet list package --vulnerable` in CI; `npm audit --audit-level=high` |
| Time & replay | Idempotency keys; JWT `jti` tracked for logout-all (V2) |

## 4. Docker socket exposure

The Worker needs the Docker Engine API to deploy. Mounting `/var/run/docker.sock` grants
root-equivalent power on the host. Mitigations for v1 and stated residual risk:

| Mitigation | Status |
| --- | --- |
| Only the **Worker** mounts the socket (never Api/Web) | v1 |
| Worker runs no user-facing HTTP except `/health` & `/metrics` on the internal network | v1 |
| Executor only issues a fixed set of calls (pull, create, start, stop, remove, inspect, logs) with container names/labels derived from `Environment.Target` (validated: `^[a-z0-9-]{3,63}$`) — never user-provided raw commands | v1 |
| Environment targets editable only by team Owner/Admin and audited | v1 |
| Docker socket proxy (`tecnativa/docker-socket-proxy`) allowing only the needed API groups | Recommended for production-like |
| Rootless Docker or remote host over TLS (`DOCKER_HOST=tcp://…` with client certs) | V2 |

Residual risk is documented in the README: the demo is intended for a single trusted host.

## 5. Data protection

| Data | Handling |
| --- | --- |
| Passwords | Identity hasher; never stored elsewhere |
| API keys | SHA-256 hash + prefix; plain shown once |
| Refresh tokens | SHA-256 hash |
| Webhook secrets | Hash stored; HMAC computed with the plain value provided at creation (kept encrypted with ASP.NET Data Protection) |
| AI content | Off by default; when on, encrypted at rest via Data Protection API column-level for `prompt_content`/`response_content`; retention job deletes after `Ai:ContentRetentionDays` (default 30) |
| Backups | `pg_dump` files contain hashes only for credentials; still treated as sensitive |

## 6. Audit coverage checklist

Every item below must emit an `AuditEvent` (verified by integration tests):

- auth: `login.succeeded`, `login.failed`, `refresh.rotated`, `refresh_reuse_detected`, `logout`, `password.changed`
- users/roles/teams: `user.created`, `user.roles_changed`, `user.deactivated`, `team.member_added/removed`
- projects/environments/api keys: `project.created/archived`, `environment.target_changed`, `apikey.created/revoked/auth_failed`
- artifacts/scans/findings: `artifact.registered`, `scan.uploaded`, `finding.suppressed/unsuppressed`
- deployments: `deployment.requested/evaluated/approved/rejected/denied/cancelled/started/succeeded/failed/rolled_back`
- policies: `policy.created/updated/enabled/disabled/archived` (with before/after diff in metadata)
- AI: `ai.request_completed`, `ai.request_blocked` (reason), `ai.policy_updated`, `ai.model_synced`
- authorization: `authz.denied` (any 403)

## 7. Security testing

- Unit tests for detectors/regex timeouts, API key hashing, JWT validation edge cases.
- Integration tests for authorization matrix (each permission × role → expected status).
- CI: Semgrep `p/csharp` + `p/owasp-top-ten`, Trivy fs/image, Gitleaks.
- Manual pre-release checklist: OWASP ASVS L1 subset (auth, session, access control,
  validation, error handling, logging).

## 8. Known gaps (v1, documented)

- No MFA; no SSO/OIDC (V2: OpenID Connect via Keycloak/Authentik or GitHub OAuth).
- No image signature verification (V2: cosign + `RequireSignedImage` rule).
- Audit log integrity is role-based, not cryptographic (V2: hash chain / WORM export).
- Single JWT signing key; rotation requires restart (V2: key ring with `kid`).
