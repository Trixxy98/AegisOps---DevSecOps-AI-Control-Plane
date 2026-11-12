#!/usr/bin/env bash
# Restore an AegisOps backup made by scripts/backup.sh (docs/operations.md).
#
# Verifies the checksums, asks you to type 'restore', stops api/worker/web (and caddy), restores the database
# into a temporary database and swaps it in only after pg_restore succeeded and the row counts match the backup,
# replaces the scan-report volume, runs `migrate` (production: re-creates the aegisops_app grants), starts the
# stack and waits for /health/ready. Uses the CURRENT .env: restore the matching .env first if it changed.
#
# Usage: scripts/restore.sh BACKUP_DIR [--project-name NAME] [--prod] [--tls] [-f FILE]... [--yes] [--dry-run]
#        scripts/restore.sh BACKUP_DIR --verify-only
#   --project-name   Compose project to restore into (default: the one `docker compose` resolves here)
#   --prod           add deploy/docker-compose.prod.yml to every compose command
#   --tls            also add deploy/docker-compose.tls.yml (implies --prod; or set AEGISOPS_TLS=1)
#   -f, --file FILE  extra Compose file (repeatable); a drill uses deploy/docker-compose.drill.yml
#   --yes            skip the typed confirmation (scripted drills)
#   --dry-run        verify the backup, then print the commands without running them
#   --verify-only    verify checksums and restore the dump into a throw-away PostgreSQL container (no network,
#                    no volume) to prove it is restorable and the row counts match; touches no Compose project
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/lib/compose-ops.sh
source scripts/lib/compose-ops.sh

backup=""
project=""
prod=false
tls=false
[[ "${AEGISOPS_TLS:-}" == 1 ]] && tls=true
assume_yes=false
dry_run=false
verify_only=false
extra_files=()
ready_timeout="${AEGISOPS_READY_TIMEOUT:-180}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --project-name) project="${2:?--project-name needs a value}"; shift 2 ;;
    --prod) prod=true; shift ;;
    --tls) tls=true; shift ;;
    -f|--file) extra_files+=("${2:?--file needs a path}"); shift 2 ;;
    --yes) assume_yes=true; shift ;;
    --dry-run) dry_run=true; shift ;;
    --verify-only) verify_only=true; shift ;;
    -h|--help) awk 'NR > 1 && /^#/ {sub(/^# ?/, ""); print; next} NR > 1 {exit}' "$0"; exit 0 ;;
    -*) echo "Unknown argument: $1" >&2; exit 2 ;;
    *)
      [[ -z "$backup" ]] || { echo "Only one BACKUP_DIR may be given." >&2; exit 2; }
      backup="$1"; shift ;;
  esac
done
[[ -n "$backup" ]] || { echo "Usage: scripts/restore.sh BACKUP_DIR [options]; see --help." >&2; exit 2; }
[[ "$ready_timeout" =~ ^[0-9]+$ ]] || ops_die "AEGISOPS_READY_TIMEOUT must be a number of seconds."
umask 077

# --- 1. Verify the backup (always, also in --dry-run) -------------------------------------------------------
[[ -d "$backup" ]] || ops_die "Backup directory not found: $backup"
backup="$(cd "$backup" && pwd)"
required=(database.dump reports.tar.gz migrations.txt table-counts.txt manifest.json)
[[ -f "$backup/checksums.sha256" ]] || ops_die "$backup/checksums.sha256 is missing; not a scripts/backup.sh backup."
for file in "${required[@]}"; do
  [[ -f "$backup/$file" ]] || ops_die "$backup/$file is missing."
  grep -Eq "^[0-9a-f]{64}  $file\$" "$backup/checksums.sha256" || ops_die "checksums.sha256 does not cover $file."
done
if grep -Evq '^[0-9a-f]{64}  (database\.dump|reports\.tar\.gz|migrations\.txt|table-counts\.txt|manifest\.json)$' \
  "$backup/checksums.sha256"; then
  ops_die "checksums.sha256 contains unexpected lines."
fi
ops_info "Verifying checksums in $backup"
if ! check_output="$(cd "$backup" && ops_sha256_check checksums.sha256 2>&1)"; then
  printf '%s\n' "$check_output" >&2
  ops_die "Checksum verification FAILED: the backup is damaged or was changed. Nothing was restored."
fi
tar -tzf "$backup/reports.tar.gz" >/dev/null || ops_die "reports.tar.gz is not a readable tar.gz archive."
ops_info "Checksums OK ($(grep -c . "$backup/migrations.txt" || true) migrations, $(grep -c '|' "$backup/table-counts.txt" || true) tables)."

# Compare the row counts of a database with the backup's table-counts.txt.
# compare_counts LABEL CMD... (CMD prints "schema.table|rows" lines)
compare_counts() {
  local label="$1"; shift
  if [[ "$dry_run" == true ]]; then
    printf '+ %s (compare with table-counts.txt)\n' "$*"
    return 0
  fi
  local actual diff_output
  if ! actual="$("$@")"; then
    echo "ERROR: Could not count rows in the $label database." >&2
    return 1
  fi
  if ! diff_output="$(diff "$backup/table-counts.txt" <(printf '%s\n' "$actual"))"; then
    printf '%s\n' "$diff_output" >&2
    echo "ERROR: Row counts in the $label database differ from the backup (< backup, > restored)." >&2
    return 1
  fi
  ops_info "Row counts match the backup ($(grep -c '|' <<<"$actual") tables)."
}

# --- 2. --verify-only: throw-away PostgreSQL, nothing else touched ------------------------------------------
if [[ "$verify_only" == true ]]; then
  image="${AEGISOPS_HELPER_IMAGE:-postgres:17-alpine}"
  name="aegisops-verify-$$"
  ops_info "Restoring the dump into a throw-away container '$name' ($image, no network, no volume)"
  if [[ "$dry_run" != true ]]; then
    trap 'docker rm -f "$name" >/dev/null 2>&1 || true' EXIT
  fi
  run_to /dev/null docker run -d --rm --name "$name" --network none -e POSTGRES_HOST_AUTH_METHOD=trust \
    -e POSTGRES_DB=aegisops_verify "$image"
  if [[ "$dry_run" != true ]]; then
    # TCP readiness: the image's init server listens on the socket only, so this waits for the real start.
    for _ in $(seq 1 60); do
      docker exec "$name" pg_isready -h 127.0.0.1 -U postgres -d aegisops_verify >/dev/null 2>&1 && break
      sleep 1
    done
    docker exec "$name" pg_isready -h 127.0.0.1 -U postgres -d aegisops_verify >/dev/null 2>&1 ||
      ops_die "The throw-away PostgreSQL did not start."
  fi
  run_from "$backup/database.dump" docker exec -i "$name" \
    pg_restore -U postgres -d aegisops_verify --no-owner --no-privileges --exit-on-error --single-transaction
  compare_counts "throw-away" docker exec "$name" psql -U postgres -d aegisops_verify -X -At -v ON_ERROR_STOP=1 -c "$OPS_COUNT_SQL" ||
    exit 1
  run_to /dev/null docker run --rm -i --network none --entrypoint tar "$image" -tzf - < "$backup/reports.tar.gz"
  ops_info "Verify-only finished: the backup is restorable. No Compose project was touched."
  exit 0
fi

# --- 3. Prepare the target project ------------------------------------------------------------------------
ops_setup_compose
volume="${project}_reports"
manifest_project="$(sed -n 's/.*"compose_project": *"\([^"]*\)".*/\1/p' "$backup/manifest.json" | head -n 1)"
manifest_mode="$(sed -n 's/.*"mode": *"\([^"]*\)".*/\1/p' "$backup/manifest.json" | head -n 1)"

# The root compose file gives its network the fixed name `aegisops`. A second project on that network would
# share the `postgres`/`redis` names with the real stack, so refuse unless an extra file (the drill) renames it.
if [[ "$dry_run" != true && ${#extra_files[@]} -eq 0 ]] && docker network inspect aegisops >/dev/null 2>&1; then
  others="$(docker ps --filter network=aegisops --format '{{.Label "com.docker.compose.project"}}' |
    grep -v -x -e "$project" -e '' | sort -u || true)"
  if [[ -n "$others" ]]; then
    ops_die "Compose project(s) '$(tr '\n' ' ' <<<"$others" | sed 's/ $//')' already use the shared 'aegisops' network. For a drill next to a running stack add -f deploy/docker-compose.drill.yml (see docs/operations.md)."
  fi
fi

target_mode="$([[ "$prod" == true ]] && echo production || echo development)"
if [[ -n "$manifest_mode" && "$manifest_mode" != "$target_mode" ]]; then
  echo "WARNING: the backup was taken in $manifest_mode mode and you restore in $target_mode mode (--prod?)." >&2
fi
echo
echo "Restore '$backup'"
echo "  from project '${manifest_project:-?}' (${manifest_mode:-?}) into project '$project' ($target_mode)"
echo "This REPLACES the database of '$project' and the contents of volume '$volume'."
echo "api, worker and web are stopped and started again; postgres keeps running. The current .env is used."
if [[ "$assume_yes" != true && "$dry_run" != true ]]; then
  read -r -p "Type 'restore' to continue: " answer
  [[ "$answer" == "restore" ]] || { echo "Aborted; nothing changed."; exit 1; }
fi

# --- 4. Stop the application, keep postgres -------------------------------------------------------------------
ops_info "Starting postgres (if needed) and stopping the application services"
run "${compose[@]}" up -d --wait postgres
app_services=(api worker web caddy)
# Read-only, also in --dry-run: only services defined in these files (caddy exists with --tls only).
active="$("${compose[@]}" config --services)" || ops_die "docker compose config failed (missing .env value?)."
to_stop=()
for service in "${app_services[@]}"; do
  if grep -qx "$service" <<<"$active"; then to_stop+=("$service"); fi
done
if [[ ${#to_stop[@]} -gt 0 ]]; then run "${compose[@]}" stop "${to_stop[@]}"; fi
ops_read_postgres_identity

# --- 5. Database: restore into <db>_restoring, check, then swap ------------------------------------------------
# A new database (rather than pg_restore --clean into the live one) gives an exact copy of the backup without
# leftovers from newer migrations, and leaves the current database untouched until the restore has succeeded.
staging_db="${pg_db}_restoring"
psql_admin=("${compose[@]}" exec -T postgres psql -U "$pg_user" -d postgres -X -v ON_ERROR_STOP=1)
ops_info "Restoring the dump into temporary database '$staging_db'"
run "${psql_admin[@]}" -c "DROP DATABASE IF EXISTS \"$staging_db\" WITH (FORCE)" -c "CREATE DATABASE \"$staging_db\" OWNER \"$pg_user\""
# --no-owner: objects belong to the restoring owner; --no-privileges: grants are re-applied by `migrate`
# (the aegisops_app role may not exist yet on a new server).
abandon_restore() {
  run "${psql_admin[@]}" -c "DROP DATABASE IF EXISTS \"$staging_db\" WITH (FORCE)" >/dev/null || true
  ops_die "$1; the current database '$pg_db' and the reports volume were NOT changed. Start the stack again with: ${compose[*]} up -d"
}
if ! run_from "$backup/database.dump" "${compose[@]}" exec -T postgres \
  pg_restore -U "$pg_user" -d "$staging_db" --no-owner --no-privileges --exit-on-error --single-transaction; then
  abandon_restore "pg_restore failed"
fi
compare_counts "restored" "${compose[@]}" exec -T postgres psql -U "$pg_user" -d "$staging_db" -X -At -v ON_ERROR_STOP=1 \
  -c "$OPS_COUNT_SQL" || abandon_restore "Row counts do not match"
ops_info "Swapping '$staging_db' in as '$pg_db'"
run "${psql_admin[@]}" -c "DROP DATABASE IF EXISTS \"$pg_db\" WITH (FORCE)" -c "ALTER DATABASE \"$staging_db\" RENAME TO \"$pg_db\""

# --- 6. Scan reports volume --------------------------------------------------------------------------------
if [[ "$dry_run" != true ]] && ! docker volume inspect "$volume" >/dev/null 2>&1; then
  ops_info "Creating volume '$volume' through Compose"
  "${compose[@]}" create --no-recreate api >/dev/null
fi
helper="$(ops_helper_image)"
ops_info "Replacing the contents of volume '$volume'"
run_from "$backup/reports.tar.gz" docker run --rm -i --network none -v "$volume:/r" --entrypoint sh "$helper" \
  -c 'find /r -mindepth 1 -maxdepth 1 -exec rm -rf {} + && tar -xzf - -C /r'

# --- 7. Migrations / grants, start, readiness ------------------------------------------------------------------
if [[ "$prod" == true ]]; then
  ops_info "Running migrate (applies newer migrations, re-creates the aegisops_app role and grants)"
  run "${compose[@]}" run --rm migrate
else
  ops_info "Development: the API applies migrations itself when it starts."
fi
ops_info "Starting the stack"
run "${compose[@]}" up -d

ops_info "Waiting up to ${ready_timeout}s for the API readiness check (from the web container)"
if [[ "$dry_run" == true ]]; then
  printf '+ %s\n' "${compose[*]} exec -T web wget -qO- http://api:8080/health/ready"
else
  ready=false
  deadline=$((SECONDS + ready_timeout))
  while ((SECONDS < deadline)); do
    if "${compose[@]}" exec -T web wget -qO- http://api:8080/health/ready >/dev/null 2>&1; then
      ready=true
      break
    fi
    sleep 3
  done
  [[ "$ready" == true ]] || ops_die "The API did not become ready in ${ready_timeout}s. Check: ${compose[*]} logs api"
fi

echo
ops_info "Restore complete for project '$project'."
echo "Next: sign in and spot-check recent deployments, scans and the audit log. Queued jobs from the backup run now."
echo "Webhook and environment secrets decrypt only with the encryption keys that were in .env when they were saved."
