# Operations guide: backups, restores, upgrades and key rotation

This guide is for the person who **keeps AegisOps running** after it is installed: on a server with the
[production profile](deploy-production.md), or on a laptop with the Development stack from the
[user manual](user-manual.md). It explains what to back up, how to restore and practise restoring, how to upgrade,
how to rotate keys and passwords, what to watch and how big the machine should be. Every step says what to type.

> **Before you start.** On a server, define the Compose command once per shell session, exactly as in
> [deploy-production.md §4](deploy-production.md#4-get-the-code-and-create-the-secrets):
>
> ```bash
> alias aegis='docker compose -f docker-compose.yml -f deploy/docker-compose.prod.yml -f deploy/docker-compose.tls.yml'
> ```
>
> On a laptop (Development), `aegis` below simply means `docker compose`. The two scripts in this guide take
> `--prod` (production profile) and `--tls` (also the Caddy file; implies `--prod`) instead of the alias.
> Run every command from the AegisOps folder.

---

## 1. What makes up "AegisOps state"

| What | Where | In `scripts/backup.sh`? | If you lose it |
| --- | --- | --- | --- |
| **Database**: users, teams, projects, policies, artifacts, scans and findings, deployments, approvals, audit log, jobs, API key hashes, encrypted webhook and environment secrets, encrypted stored AI content (only when an AI policy stores content) | Docker volume `<project>_pgdata` (PostgreSQL 17) | **Yes** (`database.dump`) | Everything is gone. |
| **Scan reports**: the original SARIF files behind every scan | Docker volume `<project>_reports`, mounted at `/app/reports` in api and worker | **Yes** (`reports.tar.gz`) | Findings stay in the database, but the "download original report" files are gone. |
| **`.env`**: database passwords, JWT signing key, the keys that encrypt webhook and environment secrets and stored AI content, domain settings | the AegisOps folder | **No, on purpose** | Encrypted secrets in the database **cannot be decrypted** any more and must be re-entered; everyone must sign in again. |
| Ollama models (optional) | Docker volume `<project>_ollama` | No | Download again with `aegis --profile ai up -d` (a few GB). |
| Redis | in memory only (`--save ""`) | No | Nothing: it is a cache and realtime channel. |
| Caddy certificates (HTTPS) | volumes `caddy_data`, `caddy_config` | No | Caddy requests new certificates on start (mind Let's Encrypt rate limits if this happens often). |
| Grafana/Prometheus data (optional) | their own volumes | No | Metric history only. |

`<project>` is the Compose project name, by default the folder name (for example `aegisops`).

> **The one rule to remember.** A database backup without the matching `.env` is only half a backup. Webhook
> signing secrets, secret environment variables and stored AI content are stored **encrypted** (AES-256-GCM) with keys that live
> only in `.env`. Keep every `.env` version for as long as you keep backups that were made with it.

## 2. Making backups

### 2.1 What the script does

```bash
scripts/backup.sh --tls                  # production with HTTPS (use --prod without Caddy)
scripts/backup.sh                        # Development (laptop)
scripts/backup.sh --tls --dry-run        # only print the commands
```

It creates `backups/aegisops-<UTC time>/` (the `backups/` folder is ignored by Git; choose another place with
`--output /var/backups/aegisops`) containing:

| File | What |
| --- | --- |
| `database.dump` | `pg_dump --format=custom` of the whole database, taken inside the `postgres` container as the owner. A consistent snapshot; the stack keeps running. |
| `reports.tar.gz` | The `reports` volume, read from a throw-away container that mounts it **read-only** and has no network. |
| `migrations.txt` | The applied EF Core migrations (`__EFMigrationsHistory`). |
| `table-counts.txt` | Rows per table, used by `restore.sh` to prove a restore is complete. |
| `manifest.json` | UTC time, project, mode, AegisOps Git commit/tag, PostgreSQL version, migrations, row totals, file sizes. |
| `checksums.sha256` | SHA-256 of every file; `restore.sh` refuses a backup whose checksums do not match. |

Good to know:

- Files are created private (`umask 077`: only your user can read them). The script never reads `.env` and never
  prints a password: it asks the `postgres` container for the user and database **names** only.
- The database is dumped first, then the reports. A report uploaded in between is an orphan in the backup; the
  Worker's cleanup removes orphaned report files after an hour.
- Any failure stops the script with a non-zero exit code and removes the half-written folder, so a folder named
  `aegisops-<time>` (without `.partial`) is always complete.
- Use `--project-name NAME` if your project is not the folder name, and `-f FILE` for extra Compose files.

### 2.2 Back up `.env` (separately)

`.env` changes rarely: copy it whenever you change it (new install, key rotation, password change), not nightly.
Store it **encrypted** and **away from the backup files**, for example in your password manager or a secrets store
your team already uses. Never commit it, mail it or paste it in a chat.

### 2.3 How often, and where to keep backups

| Setup | Suggested schedule | Keep |
| --- | --- | --- |
| Team server | nightly | 7 daily, 4 weekly, 6 monthly (adjust to your audit-retention rules) |
| Laptop / evaluation | before upgrades and before `reset-demo.sh` | the last few |

A backup that stays on the same server does not survive a lost disk or a compromised server. **Copy every backup
off the host** (another machine, object storage such as S3/B2/Azure Blob, or a NAS), encrypted.

### 2.4 Encrypting a backup at rest

The dump contains user emails, the audit log and (encrypted) secrets. Encrypt the folder before it leaves the
server. With [age](https://age-encryption.org) (keep the private key off the server):

```bash
stamp=aegisops-20261112T021500Z                        # the folder name printed by backup.sh
tar -C /var/backups/aegisops -cf - "$stamp" | age -r age1yourpublickey... > "/var/backups/aegisops/$stamp.tar.age"
# restore side:
age -d -i ~/aegisops-backup-key.txt "$stamp.tar.age" | tar -xf -
```

Or with GnuPG (symmetric; you are asked for a passphrase):

```bash
tar -C /var/backups/aegisops -cf - "$stamp" | gpg --symmetric --cipher-algo AES256 -o "/var/backups/aegisops/$stamp.tar.gpg"
gpg -d "$stamp.tar.gpg" | tar -xf -
```

### 2.5 Nightly backups with cron

Create `/usr/local/bin/aegisops-backup` (owned by root or the AegisOps user, `chmod 700`):

```bash
#!/usr/bin/env bash
set -euo pipefail
cd /opt/aegisops                                  # your AegisOps folder
out=/var/backups/aegisops
scripts/backup.sh --tls --output "$out"
latest="$(ls -1d "$out"/aegisops-*Z | tail -n 1)"
tar -C "$out" -cf - "$(basename "$latest")" | age -r age1yourpublickey... > "$latest.tar.age"
rclone copy "$latest.tar.age" offsite:aegisops-backups/   # or rsync/scp/aws s3 cp
rm -rf "$latest"                                  # keep only the encrypted copy locally
find "$out" -maxdepth 1 -name 'aegisops-*.tar.age' -mtime +7 -delete   # local copies: 7 days
```

and a crontab entry for the user that may run Docker (`crontab -e`):

```cron
15 2 * * * /usr/local/bin/aegisops-backup >> /var/log/aegisops-backup.log 2>&1
```

Set the retention of the **off-site** copies in the storage itself (lifecycle rules), so a compromised server
cannot delete them. Check the log after the first night, and alert on failures if you can (cron mails non-empty
output to the local user by default).

## 3. Restoring

### 3.1 Check a backup without touching anything

```bash
scripts/restore.sh backups/aegisops-20261112T021500Z --verify-only
```

This verifies the checksums, restores the dump into a **throw-away PostgreSQL container** (no network, no volume,
removed afterwards), compares every table's row count with the backup and checks that the reports archive can be
read. It does not stop or change your stack. Run it after copying a backup to new storage, and after every
encrypted round trip.

### 3.2 Full restore

```bash
scripts/restore.sh backups/aegisops-20261112T021500Z --tls     # production with HTTPS
scripts/restore.sh backups/aegisops-20261112T021500Z           # Development
```

The script:

1. verifies the checksums (and refuses if any file is missing, extra or changed);
2. shows what it will replace and asks you to type `restore` (`--yes` skips this for scripted drills;
   `--dry-run` prints the commands instead);
3. starts `postgres` if needed and stops `api`, `worker`, `web` (and `caddy`), so nothing writes during the restore;
4. restores the dump into a **new temporary database** `<db>_restoring` as the owner
   (`pg_restore --no-owner --no-privileges --exit-on-error --single-transaction`) and compares the row counts with
   the backup. Only when both succeed does it drop the old database and rename the new one. If anything fails,
   your current database is left as it was. (A new database, rather than `pg_restore --clean` into the live one,
   gives an exact copy without leftovers from newer migrations.);
5. empties the `reports` volume and unpacks `reports.tar.gz` into it;
6. **production only:** runs `aegis run --rm migrate`, which applies any newer migrations and re-creates the
   `aegisops_app` role, its password and its grants (grants are deliberately not taken from the dump, because the
   role may not exist yet on a new server). Development needs nothing: the API migrates when it starts;
7. starts the stack and waits (default 180 s, `AEGISOPS_READY_TIMEOUT`) until `GET /health/ready` answers, asked
   from inside the `web` container (the API image has no `curl` or `wget`).

Things to know:

- The restore uses the **current** `.env`. Restoring onto a new server? Put the `.env` that belongs to the backup
  in place first (§2.2), then run `aegis up -d --wait postgres` once and the restore.
- Data written after the backup is lost. Jobs that were queued at backup time run again when the Worker starts.
- The `postgres` user and database names come from the container; a backup can be restored into a project with a
  different name.

### 3.3 Quarterly restore drill

A backup you have never restored is a hope, not a backup. Once a quarter (and after big upgrades):

1. **Pick a recent off-site backup**, download and decrypt it (this tests the encryption keys too).
2. `scripts/restore.sh <dir> --verify-only` (§3.1).
3. **Restore into an isolated second copy** on the same server, next to the real one:

   ```bash
   scripts/restore.sh <dir> --prod --project-name aegisops-drill -f deploy/docker-compose.drill.yml --yes
   ```

   `deploy/docker-compose.drill.yml` gives the drill its own network (the main file's network has the fixed name
   `aegisops`, which a second project would otherwise share), publishes only the API on `127.0.0.1:18080` and the
   web app on `127.0.0.1:13000`, and **does not start the Worker**, so queued deployments from the backup never touch
   real containers. Do not add `--tls` to a drill. `restore.sh` refuses to restore a second project onto the shared
   network without an extra Compose file.
4. **Compare.** The script already compared the row counts with the backup. Also look at a few things by hand:
   `curl -s http://127.0.0.1:18080/health/ready`, sign in at `http://127.0.0.1:13000` (production cookies need
   HTTPS, so the session ends when you reload; that is expected) and check the newest deployments and audit events,
   and count rows in both copies:

   ```bash
   for p in aegisops aegisops-drill; do
     docker compose -p "$p" exec -T postgres psql -U aegisops -d aegisops -At -c \
       "SELECT '$p', (SELECT count(*) FROM deploy.deployments), (SELECT count(*) FROM audit.audit_events)"
   done
   ```

   The drill has the numbers of the backup time; the live copy may be a little higher.
5. **Remove the drill** (this deletes only the drill's volumes):

   ```bash
   docker compose -p aegisops-drill -f docker-compose.yml -f deploy/docker-compose.prod.yml \
     -f deploy/docker-compose.drill.yml down -v
   ```

6. Write down the date, the backup used, how long the restore took and anything that surprised you.

On a laptop leave out `--prod` (the drill then builds its own images once, which takes a few minutes). A drill on a
separate, empty machine with the saved `.env` is even better: it also proves you can rebuild from nothing.

## 4. Upgrading AegisOps

### 4.1 Production

```bash
cd /opt/aegisops
scripts/backup.sh --tls                         # 1. always back up first; note the folder name
git fetch --tags
git checkout v1.1.0                             # 2. the release tag you want (or: git pull on your branch)
less CHANGELOG.md                               # 3. read the changes since your version, especially "Security" and upgrade notes
scripts/gen-secrets.sh --upgrade                # 4. adds newly required keys to .env (prints only key names)
aegis build                                     # 5. build the new images ...
aegis run --rm migrate                          # 6. apply migrations and grants BEFORE starting the new version
aegis up -d                                     # 7. start
aegis exec web wget -qO- http://api:8080/health/ready   # 8. {"status":"Healthy",...}
```

Instead of building (step 5) you can use the release images from GHCR: `docker pull
ghcr.io/<owner>/aegisops-api:v1.1.0` (and `-worker`, `-web`), `docker tag` them as `aegisops-api:v1.1.0` etc., and
set `AEGISOPS_IMAGE_TAG=v1.1.0` in `.env`; the production file uses `aegisops-<name>:${AEGISOPS_IMAGE_TAG}` for api,
worker, web and migrate. Check the release notes for the architectures that were published.

Then sign in, look at the dashboard and the newest deployments, and watch `aegis logs -f api worker` for a few
minutes.

### 4.2 Development (laptop)

```bash
scripts/backup.sh
git pull
docker compose up --build -d                    # the API applies migrations itself in Development
```

### 4.3 Rolling back

Migrations only move forward, so "rolling back" means **going back to the backup you made in step 1**:

```bash
git checkout v1.0.0                             # the version you had before
aegis build                                     # (or set AEGISOPS_IMAGE_TAG back to the previous tag)
scripts/restore.sh backups/aegisops-<pre-upgrade time> --tls
```

Build or select the old images **before** the restore: `restore.sh` starts whatever images the current files point
to. Everything written between the backup and the rollback is lost, so decide quickly.

### 4.4 Migration policy

- Migrations are **forward-only**. AegisOps never runs `Down` migrations in operation; the way back is a restore.
- A migration that has been released (is in a tag, or applied anywhere outside a developer's laptop) is **never
  edited or deleted**. Fixes go into a new migration.
- Only the one-shot `migrate` command changes the schema in production (`Database__MigrateOnStartup` is refused
  outside Development). Run it before `up -d` after every upgrade; running it again is safe.
- Changes that remove or rename columns are split over two releases (first stop using the column, later drop it),
  so the previous version still works against the new schema during an upgrade.

## 5. Rotating secrets and passwords

Edit `.env` with your editor; never print it in a shared terminal. After every change: back up the new `.env`
(§2.2) and keep the old one as long as backups made with it exist.

To generate values:

```bash
openssl rand -base64 48 | tr -d '\n'; echo                    # JWT signing key
openssl rand -base64 32 | tr -d '\n'; echo                    # a 32-byte AES key (webhook / environment secrets / AI content)
openssl rand -base64 48 | tr -d '\n/+=' | cut -c1-32; echo    # a database password
```

Run these in your own terminal and paste the value straight into `.env`.

### 5.1 JWT signing key (`JWT_SIGNING_KEY`)

AegisOps has **one** signing key (no key ring yet, [docs/17](17-security-hardening.md)). Rotating it invalidates
every access token at once: **everyone has to sign in again**. API keys used by CI are not JWTs and keep working.

1. Put a new value in `JWT_SIGNING_KEY` (at least 32 bytes).
2. `aegis up -d api` (recreates the API with the new value).
3. Tell your users to sign in again.

Do this when the key may have leaked, when someone with access to `.env` leaves, and otherwise on a schedule you
choose (for example yearly).

### 5.2 Webhook secret encryption keys (`WEBHOOK_SECRETS_*`)

Two slots exist, `key1` and `key2`; old ciphertexts stay readable as long as their key is configured.

1. Put a new key in the unused slot (for example `WEBHOOK_SECRETS_KEY_2`) and set
   `WEBHOOK_SECRETS_CURRENT_KEY_ID=key2`. Keep the old key.
2. `aegis up -d api worker` (both need the same keys).
3. As an Admin, re-encrypt the stored secrets with the new key:

   ```bash
   read -rp "Admin email: " email; read -rsp "Password: " pw; echo
   token="$(curl -fsS https://aegisops.example.com/api/v1/auth/login -H 'Content-Type: application/json' \
     -d "{\"email\":\"$email\",\"password\":\"$pw\"}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["accessToken"])')"
   unset pw
   curl -fsS -X POST https://aegisops.example.com/api/v1/admin/webhooks/rewrap -H "Authorization: Bearer $token"
   # {"rewrapped": 3}
   unset token
   ```

   The rewrap is all-or-nothing and audited (`team.webhook_keys_rewrapped`).
4. Only after it succeeded, empty the old slot (`WEBHOOK_SECRETS_KEY_1=`) and `aegis up -d api worker` again. The
   next rotation uses `key1` again. Remember: backups taken before the rewrap still need the old key.

The webhook **signing** secret a team shares with its receiver is separate: an Owner or Admin replaces it on the
team's webhook settings (`PUT /api/v1/teams/{slug}/webhook` with a new `secretBase64`), see
[phase5-webhooks.md](phase5-webhooks.md).

### 5.3 Environment secret encryption keys (`ENVIRONMENT_SECRETS_*`)

Same idea, but **there is no automatic re-encryption** for secret environment variables yet: a stored value moves
to the new key only when someone saves it again (values are write-only, so you need the original value from your
own secret store).

1. Add `ENVIRONMENT_SECRETS_KEY_2=<new key>` and set `ENVIRONMENT_SECRETS_CURRENT_KEY_ID=key2`. Keep `KEY_1`.
2. `aegis up -d api worker`. New and re-saved secrets now use `key2`; old ones still decrypt with `key1`.
3. Re-save every secret variable (on **Projects**, each environment's **Variables** box: save the same name with
   its value again). To see how many still use the old key:

   ```bash
   aegis exec -T postgres psql -U aegisops -d aegisops -At -c \
     "SELECT count(*) FROM org.environment_variables WHERE is_secret AND protected_value LIKE 'v1.key1.%'"
   ```

4. When that prints `0`, empty `ENVIRONMENT_SECRETS_KEY_1` and `aegis up -d api worker`. A deployment that needs a
   value whose key is missing fails with the variable's name only.

### 5.4 Database passwords

**Owner (`POSTGRES_PASSWORD`).** PostgreSQL keeps the password it was **created** with; changing `.env` alone breaks
the connection. Change it inside PostgreSQL first, then in `.env`:

```bash
aegis exec postgres psql -U aegisops -d aegisops
aegisops=# \password aegisops          -- type the new password twice (not echoed, not in shell history)
aegisops=# \q
```

Put the same value in `POSTGRES_PASSWORD`, then `aegis up -d` (Development: api and worker use the owner) and, in
production, check it with `aegis run --rm migrate` (the only service that uses the owner there).

**Application role (`APP_DB_PASSWORD`, production).** Put a new value in `.env`, then:

```bash
aegis run --rm migrate          # sets the role's password from APP_DB_PASSWORD
aegis up -d api worker          # reconnect with it (a few seconds of errors in between)
```

### 5.5 API keys, user passwords and the rest

- **CI API keys:** on **Projects → your project → API keys**, create a new key, update the secret in your CI system,
  check one pipeline run, then revoke the old key. Keys also expire on their own; renew them before that.
- **User passwords:** users change their own; an Admin can **Reset password** in Administration → Users (shown once).
  Deactivate people who leave.
- **Development demo passwords:** `scripts/demo-reset-passwords.sh` (laptop only; see the user manual).
- **`GRAFANA_ADMIN_PASSWORD`:** Grafana keeps the password it first started with; change it in Grafana itself.

### 5.6 Stored AI content encryption keys (`AI_CONTENT_*`)

Only matters when an AI policy has **Store content** on. The redacted prompt and the inspected response are then
stored encrypted with the `AiContent` key ring (api only; the Worker's retention job just clears the columns and needs
no key). `scripts/gen-secrets.sh` creates `AI_CONTENT_ENABLED=true`, `AI_CONTENT_CURRENT_KEY_ID=key1` and
`AI_CONTENT_KEY_1`; for an existing `.env`, `scripts/gen-secrets.sh --upgrade` adds them, then `aegis up -d api`.
Without a key the API never stores content (the request row is marked `content_skipped`); with
`AI_CONTENT_ENABLED=true` but no current key, the API refuses to start.

Rotation works like environment secrets (§5.3): no content is rewritten, and old content simply expires.

1. Add `AI_CONTENT_KEY_2=<new key>` and set `AI_CONTENT_CURRENT_KEY_ID=key2`. Keep `KEY_1`.
2. `aegis up -d api`. New content uses `key2`.
3. Wait until content written with `key1` has expired (`Ai:ContentRetentionDays`, default 30 days). To check:

   ```bash
   aegis exec -T postgres psql -U aegisops -d aegisops -At -c \
     "SELECT count(*) FROM ai.ai_requests WHERE prompt_content LIKE 'v1.key1.%' OR response_content LIKE 'v1.key1.%'"
   ```

4. When that prints `0`, empty `AI_CONTENT_KEY_1` and `aegis up -d api`.

**Upgrading from v1.0 with Store content on.** Rows written before the upgrade hold plaintext (redacted) content;
they are not re-encrypted and are cleared by retention like any other content. To remove them at once instead:

```bash
aegis exec -T postgres psql -U aegisops -d aegisops -c \
  "UPDATE ai.ai_requests SET prompt_content = NULL, response_content = NULL
   WHERE (prompt_content IS NOT NULL AND prompt_content NOT LIKE 'v1.%')
      OR (response_content IS NOT NULL AND response_content NOT LIKE 'v1.%')"
```

## 6. Monitoring and housekeeping

### 6.1 Health

| Check | How |
| --- | --- |
| Is the API alive / ready? | `aegis exec web wget -qO- http://api:8080/health/live` and `.../health/ready` (PostgreSQL, Redis, pending migrations; Ollama shows as Degraded, not failing) |
| Is the Worker ready? | `aegis exec web wget -qO- http://worker:8081/health/ready` (also checks that it polled jobs in the last 30 s; Docker shows as Degraded) |
| All containers up? | `aegis ps` |
| In the app | the status banner (Ollama/Docker/Worker), refreshed every 30 s |
| Metrics and alerts | the observability overlay: add `-f deploy/docker-compose.observability.yml --profile observability` (Grafana `127.0.0.1:3001`, Prometheus `127.0.0.1:9090`; set `GRAFANA_ADMIN_PASSWORD`). Alerts: API/Worker down, dead-lettered jobs, stalled Worker, API error rate ([docs/15](15-observability.md)) |

From outside the server, `https://<your domain>/` should load; the API's health endpoints are not published.

### 6.2 Logs

- `aegis logs --since 1h api worker` (JSON in production, secrets masked; search for a `traceId` from an error page).
- Docker keeps container logs forever by default. Limit them in `/etc/docker/daemon.json` and restart Docker:

  ```json
  { "log-driver": "json-file", "log-opts": { "max-size": "20m", "max-file": "5" } }
  ```

### 6.3 Disk usage

```bash
df -h /var/lib/docker
docker system df                                           # images, containers, volumes, build cache
aegis exec -T postgres psql -U aegisops -d aegisops -At -c "SELECT pg_size_pretty(pg_database_size('aegisops'))"
docker run --rm -v aegisops_reports:/r:ro postgres:17-alpine du -sh /r      # the reports volume
docker builder prune --filter until=168h                   # build cache older than a week
docker image prune                                         # dangling images (old builds)
```

Never run `docker volume prune` or `down -v` on the real project: that deletes the database.

What cleans itself up (the Worker, once a day):

| Data | Rule | Setting |
| --- | --- | --- |
| Finished jobs | `Succeeded` jobs older than 7 days are deleted; `DeadLettered` jobs stay until an Admin retries them | fixed |
| Scan report files | reports of scans finished more than 90 days ago, and orphaned files after 1 hour, are deleted (findings stay) | `REPORT_RETENTION_DAYS` (`Storage__ReportRetentionDays`) |
| Stored AI prompt/response text | cleared after 30 days (only if content storage is on; hashes and previews stay) | `Ai__ContentRetentionDays` |

The **audit log is never deleted** by AegisOps. It grows slowly (hundreds of thousands of rows are fine); plan
an archive policy if your organisation requires one.

## 7. Sizing (estimates)

These are **estimates for a small team**, not measurements; watch §6.3 and adjust.

| Setup | CPU | Memory | Disk |
| --- | --- | --- | --- |
| AegisOps only, up to ~20 people, a few dozen deployments a day | 2 vCPU | 4 GB | 20 GB (OS, Docker images and build cache ~6–8 GB, database < 1 GB, reports a few GB) |
| Plus the applications AegisOps deploys on the same host | add what they need | add what they need | add their images |
| Plus Ollama with a 3B model (`llama3.2:3b`) | 4 vCPU | +4 GB (8 GB total) | +3 GB per small model |
| Plus Ollama with a 7–8B model | 4–8 vCPU (or a GPU) | +8 GB (12–16 GB total) | +5 GB per model |
| Plus the observability overlay | — | +0.5 GB | +1–2 GB metric history |
| Backups | — | — | about the size of the database plus the reports volume, per backup kept locally |

Rules of thumb: the scan-report volume grows with (scans per day) × (report size, up to 20 MiB each) × 90 days; the
database grows mostly with findings and audit events. Building images on the server needs extra memory and disk
during the build; pulling release images avoids that. CPU-only Ollama answers slowly (tens of seconds); for
interactive AI use, prefer a machine with a GPU or Apple silicon.

## 8. Troubleshooting

| Problem | What to do |
| --- | --- |
| `backup.sh`: `The postgres service of project '…' is not running` | Start it (`aegis up -d --wait postgres`) or pass the right `--project-name` (see `docker compose ls`). |
| `backup.sh`: `Docker volume '…_reports' not found` | Wrong project name, or the API has never started (the volume does not exist yet). |
| `backup.sh`: `Could not resolve the Compose project name` | `docker compose config` failed, usually a missing `.env` value; run `aegis config >/dev/null` to see the message (it prints no secrets when redirected), or pass `--project-name`. |
| `restore.sh`: `Checksum verification FAILED` | The copy is damaged or was changed. Use another copy; never "fix" the checksum file. |
| `restore.sh`: `pg_restore failed` / `Row counts do not match` | Nothing was replaced; the old database is still there. Start the stack again (the message shows the command) and try `--verify-only` on the same backup. |
| `restore.sh`: `already use the shared 'aegisops' network` | You are restoring a second project next to a running one: add `-f deploy/docker-compose.drill.yml` (§3.3), or restore into the real project name. |
| `restore.sh`: `The API did not become ready` | `aegis logs api`. Typical: `password authentication failed` (the current `.env` does not match: see §5.4), or `migrate` was not run (production). |
| After a restore, webhooks fail or deployments say a secret cannot be decrypted | The `.env` does not have the encryption key that was current when those secrets were saved. Restore that `.env` (or add the old key to a free slot) and `aegis up -d api worker`. |
| After a restore, everyone is signed out | Expected if the JWT key changed; sign in again. |
| `password authentication failed for user "aegisops"` after changing `.env` | The volume keeps its first password: change it inside PostgreSQL (§5.4) or put the old value back. |
| `permission denied for table …` after a restore | The grants were not re-applied: `aegis run --rm migrate`, then `aegis up -d`. |
| Disk full | §6.3: build cache and old images first, then Docker logs, then report retention. Do not delete volumes. |
| Upgrade went wrong | §4.3: previous version plus the pre-upgrade backup. |

## 9. What this guide does not cover yet

- Point-in-time recovery (WAL archiving) and replicas: backups are nightly snapshots, so you can lose up to a day.
- Automatic re-encryption of environment secrets (and of stored AI content, which expires instead), and a JWT key
  ring with overlapping keys.
- High availability: one server, one copy of each service.

Related: [deploy-production.md](deploy-production.md) (installation), [docs/14](14-devops-and-infrastructure.md)
(Compose and scripts), [docs/15](15-observability.md) (metrics and alerts), [docs/17](17-security-hardening.md)
(security controls).
