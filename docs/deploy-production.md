# Run AegisOps on a server (production profile with HTTPS)

This guide puts AegisOps on **one Linux server** with a real domain name and HTTPS, so your team can use it from
their browsers and your CI can report to it. It is written for beginners: every step says what to type and what you
should see. If you only want to try AegisOps on your laptop, use the [user manual](user-manual.md) instead.

> **What this setup is, and is not.** One server, one copy of every service, data in Docker volumes on that
> server. It is **not** highly available (if the server stops, AegisOps stops) and **not** multi-tenant (one
> organisation per installation). Some security items are still open; see [section 11](#11-what-is-not-covered-yet).

---

## 1. What changes compared to the laptop (Development) setup

| | Laptop (Development) | Server (this guide) |
| --- | --- | --- |
| Start command | `docker compose up -d` | the same plus `-f deploy/docker-compose.prod.yml -f deploy/docker-compose.tls.yml` |
| ASP.NET environment | `Development` | `Production`: JSON logs, no developer error pages, errors as Problem Details with a `traceId` |
| Demo users, team, projects, policies | created automatically | **none**: you create the first Admin with `bootstrap-admin` |
| Database migrations | applied automatically when the API starts | applied by a one-shot `migrate` command (before the first start and after every upgrade) |
| Address | `http://127.0.0.1:3000` | `https://aegisops.example.com` (Caddy, automatic certificate, HSTS) |
| Published ports | 3000, 8080, 5432, 6380 on loopback | **only 80 and 443** (Caddy); PostgreSQL, Redis, API and web are internal |
| Unsafe settings | allowed | the API **refuses to start** with the `.env.example` JWT key or the Development database password |
| Database user of api and worker | the owner `aegisops` | the restricted role `aegisops_app` (`APP_DB_PASSWORD`): it can read and add audit rows but never change or delete them; only `migrate` uses the owner |
| Rate limits | the same | 300 requests per user or API key (token bucket, 50 back every 10 s), 30 scan uploads, 10 sign-ins per minute per client address; `429` with `Retry-After` |

## 2. What you need

- A Linux server (a small VPS is fine) with **2 CPUs, 4 GB of memory and 20 GB of disk** for AegisOps itself.
  Add about 4 GB of memory if you run the local AI model (Ollama) on the same server, and room for the
  applications AegisOps deploys.
- **Docker Engine** with the **Compose plugin** (`docker compose version` prints v2.24 or newer; the overrides use
  `!reset`).
- A **domain name** you control, for example `aegisops.example.com`.
- Ports **80 and 443** open to the internet (or to your company network). Port 80 is needed for the certificate
  check and redirects to HTTPS.
- `git` and `openssl`.

## 3. Point the domain at the server (DNS)

At your DNS provider, create an **A record** (and an **AAAA record** if the server has IPv6):

```
aegisops.example.com.   A   203.0.113.10     # your server's public IP
```

Check from your laptop: `dig +short aegisops.example.com` prints the server's IP. Wait until it does; Caddy can
only get a certificate once the name resolves to the server.

## 4. Get the code and create the secrets

On the server:

```bash
git clone <your fork or the AegisOps repository URL> aegisops
cd aegisops
scripts/gen-secrets.sh              # writes .env (mode 600) with random keys; never commit it
```

Then add three lines to `.env` with your editor (no secrets here, just names):

```bash
AEGISOPS_DOMAIN=aegisops.example.com   # the DNS name from step 3
ACME_EMAIL=ops@example.com             # Let's Encrypt sends certificate expiry notices here
DOCKER_GID=988                         # output of: stat -c %g /var/run/docker.sock
```

`DOCKER_GID` lets the Worker use the Docker socket without running as root (see
[docs/14](14-devops-and-infrastructure.md) for the socket-proxy alternative). **Keep `.env` private and backed up**:
it holds the database passwords (`POSTGRES_PASSWORD` for the owner, `APP_DB_PASSWORD` for the restricted role of
the api and worker), the JWT signing key and the keys that encrypt webhook and environment secrets.
Losing the encryption keys means re-entering every secret.

> If you copied `.env.example` instead of running `gen-secrets.sh`, the API will refuse to start in Production
> with a message naming the setting to fix. That is on purpose.

To save typing, define the Compose command once per shell session:

```bash
alias aegis='docker compose -f docker-compose.yml -f deploy/docker-compose.prod.yml -f deploy/docker-compose.tls.yml'
```

## 5. Build and prepare the database

```bash
aegis build                          # builds aegisops-api, aegisops-worker and aegisops-web (a few minutes)
aegis run --rm migrate               # creates the tables, the four roles and the database role aegisops_app, then exits
```

You should see `Database migrations are applied.`, `Created roles: Admin, Developer, Security, Approver.` and
`Database role aegisops_app is up to date (audit: SELECT and INSERT only).` Running `migrate` again is safe; it then
prints `Roles already present: …` and re-applies the database grants (it also sets the role's password from
`APP_DB_PASSWORD`, so changing that value and re-running `migrate` rotates it).

`migrate` is the only service that connects as the database owner `aegisops`. The api and worker connect as
`aegisops_app`, which has normal read/write rights on the application schemas but only `SELECT` and `INSERT` on
`audit.*` (no `UPDATE`, `DELETE` or `TRUNCATE`; tables added by later migrations get the same grants). A bug or an
attacker inside the API can therefore not rewrite the audit log; someone with the owner password still can (see
[docs/17 §2](17-security-hardening.md)).

## 6. Create the first Admin

```bash
aegis run --rm api bootstrap-admin --email you@example.com --display-name "Your Name"
```

The output ends with:

```
Created Admin you@example.com (Your Name).
Temporary password (shown only once): <24 random characters>
Sign in with it now; AegisOps makes you change it at first sign-in.
```

Copy the temporary password now: it is **not** stored anywhere you can read it later and is never written to logs
or the audit log. The audit log records a `user.bootstrapped` event by `bootstrap-admin`.

`bootstrap-admin` **refuses** (exit code 1) as soon as any Admin exists, so it cannot be used later to slip in an
extra Admin. Lost the first password before signing in? On a fresh installation with no data yet, start the
database over: `aegis down`, `docker volume rm aegisops_pgdata` (the prefix is the Compose project name, by default
the folder name), then repeat steps 5–7.

## 7. Start AegisOps

```bash
aegis up -d
aegis ps                             # caddy, web, api, worker, postgres and redis are "running"
aegis logs -f caddy                  # wait for "certificate obtained successfully", then Ctrl+C
```

Open `https://aegisops.example.com`, sign in with your email and the temporary password, and choose a new password
(at least 12 characters).

Optional extras use the same command with a profile, for example the local AI model:
`aegis --profile ai up -d`.

## 8. First steps after signing in

The production profile starts **empty**: no demo team, projects or policies. As the Admin:

1. **Teams**: create your team on the Teams screen.
2. **Users**: add your team in **Administration → Users → Add user** (email, display name, roles, and optionally
   the team as Member or Owner). Each new user gets a temporary password shown once; give it to them privately,
   and they choose their own at first sign-in. Consider a second Admin so a lost Admin password can be reset.
   Existing users join further teams with `PUT /api/v1/teams/{slug}/members/{userId}` ([docs/10](10-api-specification.md)).
3. **Policies**: create at least a Global policy for Production (for example "tests must pass" and "two approvals")
   on the Policies screen; the [policy engine doc](05-policy-engine.md) lists every rule.
4. **AI** (only if you run Ollama): sync models, enable one and save a Global AI policy. Without an enabled AI policy
   the gateway answers `503 PolicyUnavailable`.
5. **Connect a repository**: follow [connect your repository](connect-your-repository.md), with
   `AEGISOPS_URL=https://aegisops.example.com` instead of `http://127.0.0.1:8080`. With HTTPS, GitHub-hosted runners
   can reach AegisOps directly; a self-hosted runner is no longer required (keep API keys scoped and rotate them).

> **Lost passwords.** An Admin resets a user's password in Administration → Users (**Reset password**); the new
> temporary password is shown once. Deactivate people who leave instead of deleting them, so the audit log keeps
> their history. The last active Admin cannot be deactivated or lose the Admin role.

## 9. Day-two operations

### Backups

Everything important is in the `pgdata` volume (database) and the `reports` volume (uploaded scan reports), plus
your `.env`. A simple nightly database backup:

```bash
aegis exec -T postgres pg_dump -U aegisops -d aegisops --format=custom > aegisops-$(date +%F).dump
```

Copy the dump and `.env` off the server. Restore into an empty database with `pg_restore`. A fuller
operations guide (restore drill, sizing, key rotation) is roadmap v1.1 item 9.

### Upgrading

```bash
git pull                             # or check out the release tag you want
aegis build
aegis run --rm migrate               # always before starting the new version
aegis up -d
```

Take a backup first. Migrations only move forward; to go back to an older version, restore the backup.

**Upgrading from a version without `APP_DB_PASSWORD`** (before the audit-role change): Compose stops with
`Set APP_DB_PASSWORD in .env`. Add it, create the role, then restart:

```bash
scripts/gen-secrets.sh --upgrade     # appends a random APP_DB_PASSWORD to .env; prints only the key name
aegis run --rm migrate               # creates aegisops_app and its grants
aegis up -d                          # api and worker now connect as aegisops_app
```

### Logs and health

- `aegis logs api worker` shows JSON logs (one event per line, secrets masked). An error page or Problem Details
  response carries a `traceId`; search the API log for it.
- Readiness from the server itself: `aegis exec web wget -qO- http://api:8080/health/ready` (or use the
  observability profile, [docs/15](15-observability.md)).

### How the HTTPS chain works

```
browser ──HTTPS──► caddy (80/443) ──HTTP──► web / nginx ──HTTP──► api
                    sets X-Forwarded-For     appends caddy's       trusts X-Forwarded-For/-Proto only from
                    and X-Forwarded-Proto    address                the Docker network, two hops deep
```

The API's login rate limit (and the per-address limit of anonymous requests) and the refresh-token records therefore
see the **real client address**, not nginx's.
The trust is configured in `deploy/docker-compose.tls.yml`:

| Setting | Value | Meaning |
| --- | --- | --- |
| `ForwardedHeaders__Enabled` | `true` | honour forwarded headers at all (off in every other setup) |
| `ForwardedHeaders__KnownNetworks__0` | `172.16.0.0/12` (`AEGISOPS_PROXY_NETWORK`) | only proxies inside Docker's default network range are trusted |
| `ForwardedHeaders__ForwardLimit` | `2` | unwind Caddy and nginx, nothing more |

If `docker network inspect aegisops --format '{{(index .IPAM.Config 0).Subnet}}'` prints a subnet outside
`172.16.0.0/12`, set `AEGISOPS_PROXY_NETWORK` to it in `.env` and run `aegis up -d api`. If the setting is wrong,
nothing breaks; the API just sees nginx's address again.

### Using your own reverse proxy instead of Caddy

Leave out `deploy/docker-compose.tls.yml`. The production profile alone keeps web on `127.0.0.1:3000`; point your
proxy (nginx, Traefik, a load balancer) at it, terminate TLS there, send HSTS, and set
`ForwardedHeaders__Enabled`, `ForwardedHeaders__KnownNetworks__0` (or `ForwardedHeaders__KnownProxies__0`) and
`ForwardedHeaders__ForwardLimit` for the API to match your proxy chain. Never trust `0.0.0.0/0`.

## 10. Troubleshooting

| Problem | What to do |
| --- | --- |
| `api` keeps restarting; log says `Refusing to start in the Production environment` | The message names the setting: run `scripts/gen-secrets.sh` (or fix `.env`) and `aegis up -d`. |
| `Automatic migrations are only supported in Development` | Something sets `Database__MigrateOnStartup=true`; remove it and use `aegis run --rm migrate`. |
| API errors like `relation … does not exist` | Migrations were not applied: `aegis run --rm migrate`. |
| `password authentication failed for user "aegisops_app"` | The role does not exist yet or `APP_DB_PASSWORD` changed since the last `migrate`: run `aegis run --rm migrate`, then `aegis up -d`. |
| `permission denied for table …` in the api/worker log | The grants are older than the table: run `aegis run --rm migrate` (it re-applies them). On `audit.*` an `UPDATE`/`DELETE` is denied on purpose. |
| `429 Too Many Requests` | A user, API key or client address went over its budget; wait for `Retry-After` seconds. Limits are set with `RateLimits__*` ([docs/14](14-devops-and-infrastructure.md)). |
| PostgreSQL rejects the password after you regenerated `.env` | An existing `pgdata` volume keeps its first password. Put the old `POSTGRES_PASSWORD` back, or change it inside PostgreSQL (`ALTER USER aegisops PASSWORD …`). |
| Caddy log: certificate errors / `NXDOMAIN` / timeouts | DNS does not point at the server yet, or ports 80/443 are blocked by a firewall. Fix and `aegis restart caddy`. |
| Browser shows a certificate warning | Caddy has not obtained the certificate yet; check `aegis logs caddy`. |
| `bootstrap-admin: refused. An Admin already exists` | Sign in as that Admin. A second Admin can reset a lost password (Administration → Users → **Reset password**). With a single Admin and no data yet, start over as in [step 6](#6-create-the-first-admin); with data, there is no supported recovery path yet. |
| Scan upload fails with `413` | The report is over 20 MiB. |
| Sign-in works on the server but the session ends at every reload | The refresh cookie is `Secure`; it needs HTTPS. Use the `https://` address, not `http://server-ip:3000`. |

## 11. What is not covered yet

- **High availability and multiple organisations**: one server, one organisation.
- **Open security items** from the [v1 security review](security-review-v1.md): policy branch globs without
  `NonBacktracking`, stored AI content not encrypted at rest. Audit integrity is role-based, not cryptographic, and
  rate limits are kept in memory per API instance. The Worker mounts the Docker socket, which is
  root-equivalent on the host ([docs/17 §4](17-security-hardening.md)).
- **User management**: no self-service sign-up, invitation email or account deletion (deactivate instead); there
  is no screen to change team membership of existing users yet (API only).
- **Single sign-on, MFA, email**: accounts are local; temporary passwords are handed over by an Admin.
- **Operations guide** (restore drills, key rotation, sizing): [roadmap v1.1](roadmap-v1.1.md) item 9.
