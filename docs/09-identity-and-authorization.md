# 09 — Identity & Authorization

## 1. Two questions, two mechanisms

| Question | Mechanism | Example |
| --- | --- | --- |
| *May this principal perform this action on this resource?* | **Authorization** (this document): roles → permissions → ASP.NET Core policy-based authorization with resource handlers | Aina (Developer, member of team X) may request a deployment for a project in team X |
| *May this change reach this environment?* | **Policy engine** ([05](05-policy-engine.md)) | Aina's request to Production requires 2 approvals |

Keeping them separate avoids the classic mistake of encoding release rules as role checks.

## 2. Principals

| Principal | Authenticates with | Claims |
| --- | --- | --- |
| **User** | Email + password → JWT access token (15 min) + refresh token (httpOnly cookie, 7 days, rotating) | `sub`, `email`, `name`, `role[]`, `team:<teamId>:<Owner\|Member>[]`, `jti` |
| **API key (CI)** | `Authorization: Bearer aok_<prefix>_<secret>` | `sub = apikey:<id>`, `project = <projectId>`, `scope[]`, `actor_type = ApiKey` |
| **System** | Worker in-process | Used for audit attribution of automated transitions |

Authentication handlers: `JwtBearer` (default) and a custom `ApiKeyAuthenticationHandler`
selected by the `aok_` prefix via a policy scheme (`"Smart"` scheme forwards to the right one).

## 3. Identity (ASP.NET Core Identity)

- `IdentityUser<Guid>`-based `User`; roles via `IdentityRole<Guid>`.
- Password policy: min 12 chars, no composition rules (NIST 800-63B), breached-password
  check against a local top-100k list (V2).
- Lockout: 5 failures → 15 min.
- Email confirmation: **disabled** in v1 (no mail server); admin creates users and sets a
  temporary password (`MustChangePassword` flag). Seeded accounts start with it too. While it is set, access
  tokens carry `pwd_change=true` and the API answers every route except `/auth/me`, `/auth/change-password`,
  `/auth/refresh` and `/auth/logout` with 403 Problem Details (`reason: PasswordChangeRequired`); the SPA shows a
  "Choose a new password" step and signs in again with the new password.
- Refresh tokens stored hashed (`RefreshToken.TokenHash`); reuse of a rotated token revokes
  the whole family and is audited (`auth.refresh_reuse_detected`, `Outcome = Denied`, owner as actor) in the
  same save as the revocation. Each successful rotation is audited as `auth.refreshed`.
- Failed logins (unknown email, wrong password, inactive user) are audited as `auth.login_failed`
  (`ActorType = Anonymous`, display `anonymous`, `Outcome = Denied`, `ResourceId` = the account id only when it
  exists). The submitted email and password are never recorded.
- MFA (TOTP): V2.

### Auth endpoints

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/v1/auth/login` | Validates credentials → access token in body, refresh token as `Set-Cookie` (`HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`) |
| `POST /api/v1/auth/refresh` | Rotates refresh token, returns new access token |
| `POST /api/v1/auth/logout` | Revokes current refresh token family |
| `GET /api/v1/auth/me` | Current user, roles, teams, permissions |
| `POST /api/v1/auth/change-password` | Requires current password |
| `POST /api/v1/admin/users/{id}/reset-password` | Admin (`users:manage`): sets a random temporary password returned once (`Cache-Control: no-store`), sets `MustChangePassword`, clears lockout, revokes the user's refresh tokens; audited as `user.password_reset` without the password |

Rate limiting on `login`/`refresh`: 10 req/min per IP (ASP.NET rate limiter, fixed window).

## 4. Roles and permissions

Roles are coarse groupings; permissions are what code checks. Mapping lives in a single
`Permissions` static class + `RolePermissions` dictionary (Application layer) and is exposed
to the UI via `/auth/me` so buttons can hide themselves.

| Permission | Admin | Developer | Security | Approver |
| --- | :-: | :-: | :-: | :-: |
| `teams:read`, `projects:read` (own teams) | ✓ | ✓ | ✓ | ✓ |
| `teams:write`, `projects:write` | ✓ | Owner of team | — | — |
| `environments:write` | ✓ | Owner of team | — | — |
| `apikeys:manage` | ✓ | Owner of team | — | — |
| `artifacts:read`, `scans:read`, `findings:read` | ✓ | ✓ | ✓ | ✓ |
| `artifacts:write`, `scans:write` (manual registration/upload) | ✓ | — | ✓ | — |
| `findings:suppress` | ✓ | — | ✓ | — |
| `deployments:read` | ✓ | ✓ | ✓ | ✓ |
| `deployments:request` | ✓ | ✓ (team member) | — | — |
| `deployments:cancel` | ✓ | requester / team Owner | — | — |
| `deployments:approve` | ✓ | — | ✓ | ✓ |
| `policies:read` | ✓ | ✓ | ✓ | ✓ |
| `policies:write` | ✓ | — | ✓ | — |
| `ai:chat` | ✓ | ✓ | ✓ | ✓ |
| `ai:requests:read` (all users') | ✓ | own only | ✓ | own only |
| `ai:policies:write` | ✓ (Global + Team) | — | ✓ (Team) | — |
| `ai:policies:read` (policy, satisfied by `ai:policies:write`) | ✓ | — | ✓ | — |
| `audit:read` | ✓ | — | ✓ | — |
| `users:manage`, `roles:manage`, `models:manage` | ✓ | — | — | — |

A user may hold multiple roles (e.g. `Security` + `Approver`).

How the narrowed cells are enforced:

- A narrowed grant ("Owner of team", "team member", "requester / team Owner", "own only") is granted to
  the role in `RolePermissions`, so the route permission admits it, and the handler then narrows it.
- "Owner of team" (`teams:write`, `projects:write`, `environments:write`, `apikeys:manage`): the handler
  allows `Admin` or an `Owner` of the target team (for a project, the project's team). A `Member` or a
  non-member gets `404` so the resource is not revealed. `Security`/`Approver` do not hold these
  permissions, so they get `403` even if they are a team Owner. The team write routes (`PATCH
  /teams/{slug}`, `PUT`/`DELETE /teams/{slug}/members/{userId}`, team webhook settings, deliveries,
  summary and `POST /teams/{slug}/webhook/deliveries/{eventId}/replay`) require `teams:write`.
- Creating a team (`POST /teams`) requires `users:manage` (Admin): the "Owner of team" condition cannot
  apply to a team that does not exist yet. The creator becomes its Owner.
- `ai:policies:read` is a route policy, not a role permission: `GET /ai/policies` is allowed to whoever
  holds `ai:policies:write`.

## 5. Policy-based authorization in ASP.NET Core

Endpoints declare **requirements**, not roles:

```csharp
app.MapPost("/api/v1/deployments", RequestDeployment.Handle)
   .RequireAuthorization(AuthPolicies.DeploymentsRequest);   // permission check

// inside the handler, a resource-based check:
var authz = await authorizationService.AuthorizeAsync(user, project, Operations.RequestDeployment);
```

| Requirement / handler | Checks |
| --- | --- |
| `PermissionRequirement(permission)` | Principal's roles map to permission (users) or API key `scope` covers it (keys) |
| `ProjectMemberHandler` | User is member of `project.TeamId` or `Admin`; API key's `project` claim equals project |
| `ApproveDeploymentHandler` | `deployments:approve` **and** approver ≠ requester **and** (team member or Admin) **and** role ∈ roles required by the policy evaluation |
| `CancelDeploymentHandler` | requester, team Owner, or Admin |
| `SuppressFindingHandler` | `findings:suppress` and project access |
| `EditPolicyHandler` | Global → Admin; Team → Security/Admin; Project → Admin, or Security who is a member of the project's team. Enforced on create, enable/disable, archive and replace (`PolicyEditAccess`); otherwise `403` |

Denied authorization returns `403` with Problem Details and writes an `AuditEvent` with
`Outcome = Denied` (so attempted privilege abuse is visible). For route-permission denials this is
done by `AuditingAuthorizationResultHandler` (an `IAuthorizationMiddlewareResultHandler`): the body is a
generic RFC 7807 Problem Details (`status: 403`, no policy or internal details), and the event has
`Action = authorization.denied`, `ResourceType = Endpoint`, and the actor (user or API key) when
authenticated. The request body and query string are never recorded. If the audit write fails, the
failure is logged and the response is still `403`. Unauthenticated requests still get the normal `401`
challenge, which is not audited by this handler.

## 6. Team scoping

- Every `Project` belongs to exactly one `Team`.
- A user sees only projects of teams they belong to, unless `Admin` (or `Security`, who
  sees all projects read-only for review purposes: the project list and detail handlers skip team
  scoping for both roles, and Security holds no project write permission).
- Query filters implement scoping in Application read handlers (`IProjectAccess.
  VisibleProjectIds(user)`), not via EF global filters (explicit > magic).

## 7. API keys (CI service principals)

| Aspect | Design |
| --- | --- |
| Format | `aok_<8-char prefix>_<32-byte base64url secret>`; prefix stored plain for lookup, secret stored as SHA-256 hash |
| Scope | Bound to one `Project`; scopes subset of `artifacts:write`, `scans:write`, `deployments:request` |
| Lifetime | `ExpiresAt` required (max 1 year); `LastUsedAt` updated at most once per minute |
| Revocation | `RevokedAt` set; immediate effect (no caching of key validity beyond 60 s) |
| Display | Plain key shown exactly once at creation |
| Audit | `apikey.created`, `apikey.revoked`, `apikey.auth_failed` (`Outcome = Denied`) only when the prefix matches an existing key (wrong secret or expired key). Actor is that key, and the display is its 8-character prefix, never the secret. Malformed or unknown keys get `401` without an audit row, so unauthenticated traffic cannot grow the immutable audit table. A failing audit write is logged and the response stays `401`. Actions performed with a key are recorded with `ActorType = ApiKey` and the key id. |
| Deployment target restriction | API keys may request deployments to **Development/Staging** only by default; `Target.allowApiKeyProduction = true` on an environment opts in (production usually starts from a human or from an approved promotion) |

## 8. Front-end session model

- Access token kept **in memory** (never `localStorage`); refresh via cookie on 401 using an
  Axios interceptor with a single in-flight refresh promise.
- SignalR connects with `accessTokenFactory` returning the current access token.
- `/auth/me` result cached in TanStack Query; permissions drive UI affordances, the API is
  the enforcement point.

## 9. Seeded demo accounts

| Email | Roles | Teams |
| --- | --- | --- |
| `admin@aegisops.local` | Admin | — |
| `aina@aegisops.local` | Developer | `network-platform` (Member) |
| `rith@aegisops.local` | Developer | `network-platform` (Owner) |
| `farid@aegisops.local` | Security, Approver | — |
| `mei@aegisops.local` | Approver | — |

Seed passwords are generated at first start and printed once to the API log
(`Seed:PrintPasswords = true` only in `Development`).
