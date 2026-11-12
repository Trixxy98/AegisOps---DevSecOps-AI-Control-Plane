# Shared helpers for scripts/backup.sh and scripts/restore.sh (docs/operations.md). Sourced, not executed.
#
# The caller sets: dry_run (true/false), prod (true/false), tls (true/false), project (may be empty),
# extra_files (array of extra Compose files). ops_setup_compose then fills `compose` (the docker compose
# command with -p and every -f) and validates the project name.

# Tables counted in a backup and compared after a restore: every table outside the PostgreSQL catalogs.
# Output: one "schema.table|rows" line per table, sorted.
OPS_COUNT_SQL="SELECT format('%s.%s|%s', schemaname, tablename,
  (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', schemaname, tablename), false, true, '')))[1])
FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema') ORDER BY 1"

# Applied EF Core migrations, oldest first (EF's default history table in the public schema).
# Snake-case naming convention: EF's history table keeps its name but its columns are snake_case.
OPS_MIGRATIONS_SQL='SELECT migration_id FROM "__EFMigrationsHistory" ORDER BY migration_id'

ops_die() {
  echo "ERROR: $*" >&2
  exit 1
}

ops_info() {
  printf '==> %s\n' "$*"
}

# Run a command, or print it in --dry-run mode.
run() {
  if [[ "$dry_run" == true ]]; then
    printf '+ %s\n' "$*"
  else
    "$@"
  fi
}

# run_to FILE CMD...: run CMD with stdout written to FILE.
run_to() {
  local file="$1"; shift
  if [[ "$dry_run" == true ]]; then
    printf '+ %s > %s\n' "$*" "$file"
  else
    "$@" > "$file"
  fi
}

# run_from FILE CMD...: run CMD with stdin read from FILE.
run_from() {
  local file="$1"; shift
  if [[ "$dry_run" == true ]]; then
    printf '+ %s < %s\n' "$*" "$file"
  else
    "$@" < "$file"
  fi
}

ops_setup_compose() {
  local files=(-f docker-compose.yml)
  if [[ "$tls" == true ]]; then prod=true; fi
  if [[ "$prod" == true ]]; then files+=(-f deploy/docker-compose.prod.yml); fi
  if [[ "$tls" == true ]]; then files+=(-f deploy/docker-compose.tls.yml); fi
  local extra
  for extra in "${extra_files[@]+"${extra_files[@]}"}"; do
    [[ -f "$extra" ]] || ops_die "Compose file not found: $extra"
    files+=(-f "$extra")
  done
  if [[ -z "$project" ]]; then
    # `config` resolves secrets from .env; only the `name:` line is kept, nothing else is printed.
    project="$(docker compose "${files[@]}" config 2>/dev/null | sed -n 's/^name: *//p' | head -n 1 || true)"
    [[ -n "$project" ]] || ops_die "Could not resolve the Compose project name; pass --project-name."
  fi
  if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]]; then
    ops_die "Unexpected Compose project name '$project'."
  fi
  compose=(docker compose -p "$project" "${files[@]}")
}

# Print one environment variable of the running postgres container (names only; never passwords).
ops_postgres_env() {
  "${compose[@]}" exec -T postgres printenv "$1" 2>/dev/null || true
}

# Read POSTGRES_USER and POSTGRES_DB from the container into pg_user/pg_db.
ops_read_postgres_identity() {
  if [[ "$dry_run" == true ]]; then
    pg_user='$POSTGRES_USER'
    pg_db='$POSTGRES_DB'
    return
  fi
  pg_user="$(ops_postgres_env POSTGRES_USER)"
  pg_user="${pg_user:-postgres}"
  pg_db="$(ops_postgres_env POSTGRES_DB)"
  pg_db="${pg_db:-$pg_user}"
  [[ "$pg_user" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || ops_die "Unexpected POSTGRES_USER in the postgres container."
  [[ "$pg_db" =~ ^[a-z_][a-z0-9_]*$ ]] || ops_die "Unexpected POSTGRES_DB in the postgres container."
}

ops_require_running_postgres() {
  [[ "$dry_run" == true ]] && return 0
  local running
  running="$("${compose[@]}" ps --status running --services 2>/dev/null || true)"
  grep -qx postgres <<<"$running" ||
    ops_die "The postgres service of project '$project' is not running. Start it first: ${compose[*]} up -d --wait postgres"
}

# Image of the running postgres container: always present locally, has tar, gzip and psql.
ops_helper_image() {
  if [[ -n "${AEGISOPS_HELPER_IMAGE:-}" ]]; then
    printf '%s\n' "$AEGISOPS_HELPER_IMAGE"
    return
  fi
  local id image=""
  if [[ "$dry_run" != true ]]; then
    id="$("${compose[@]}" ps -q postgres 2>/dev/null | head -n 1 || true)"
    if [[ -n "$id" ]]; then image="$(docker inspect --format '{{.Config.Image}}' "$id" 2>/dev/null || true)"; fi
  fi
  printf '%s\n' "${image:-postgres:17-alpine}"
}

ops_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$@"
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$@"
  else
    ops_die "Neither sha256sum nor shasum is installed."
  fi
}

ops_sha256_check() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c "$@"
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 -c "$@"
  else
    ops_die "Neither sha256sum nor shasum is installed."
  fi
}

ops_file_size() {
  wc -c < "$1" | tr -d '[:space:]'
}

ops_secrets_reminder() {
  cat <<'TEXT'
REMINDER: this backup does NOT contain .env. Back up .env separately and securely (for example in a password
manager or an encrypted secrets store), never next to the backup files in plain text. It holds the database
passwords, the JWT signing key and the encryption keys of webhook and environment secrets: the encrypted values
in the database dump are useless without those keys. See docs/operations.md.
TEXT
}
