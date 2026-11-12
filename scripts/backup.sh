#!/usr/bin/env bash
# Back up AegisOps state: the PostgreSQL database and the scan-report volume (docs/operations.md).
#
# Writes DIR/aegisops-<UTC timestamp>/ with database.dump (pg_dump custom format), reports.tar.gz (the
# `reports` volume), migrations.txt, table-counts.txt, manifest.json and checksums.sha256. Files are private
# (umask 077). The stack keeps running; .env is NOT copied (back it up separately and securely).
#
# Usage: scripts/backup.sh [--output DIR] [--project-name NAME] [--prod] [--tls] [-f FILE]... [--dry-run]
#   --output         parent directory for the backup (default: ./backups, ignored by Git)
#   --project-name   Compose project to back up (default: the one `docker compose` resolves here)
#   --prod           add deploy/docker-compose.prod.yml to every compose command
#   --tls            also add deploy/docker-compose.tls.yml (implies --prod; or set AEGISOPS_TLS=1)
#   -f, --file FILE  extra Compose file (repeatable), e.g. deploy/docker-compose.drill.yml
#   --dry-run        print the commands without running them
set -euo pipefail
cd "$(dirname "$0")/.."
# shellcheck source=scripts/lib/compose-ops.sh
source scripts/lib/compose-ops.sh

output="backups"
project=""
prod=false
tls=false
[[ "${AEGISOPS_TLS:-}" == 1 ]] && tls=true
dry_run=false
extra_files=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) output="${2:?--output needs a directory}"; shift 2 ;;
    --project-name) project="${2:?--project-name needs a value}"; shift 2 ;;
    --prod) prod=true; shift ;;
    --tls) tls=true; shift ;;
    -f|--file) extra_files+=("${2:?--file needs a path}"); shift 2 ;;
    --dry-run) dry_run=true; shift ;;
    -h|--help) awk 'NR > 1 && /^#/ {sub(/^# ?/, ""); print; next} NR > 1 {exit}' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

umask 077
ops_setup_compose
ops_require_running_postgres
ops_read_postgres_identity

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
final="$output/aegisops-$stamp"
work="$final.partial"
[[ -e "$final" || -e "$work" ]] && ops_die "$final already exists."
volume="${project}_reports"
helper="$(ops_helper_image)"

if [[ "$dry_run" != true ]]; then
  docker volume inspect "$volume" >/dev/null 2>&1 || ops_die "Docker volume '$volume' not found (wrong --project-name?)."
  mkdir -p "$output"
  mkdir "$work"
  # Never leave a half-written backup that looks complete.
  completed=false
  trap 'if [[ "$completed" != true ]]; then rm -rf "$work"; echo "Backup FAILED; nothing was kept." >&2; fi' EXIT
fi

ops_info "Backing up project '$project' into $final"

# 1. Database first (a consistent snapshot); reports written after it are harmless orphans.
ops_info "Dumping database '$pg_db' (pg_dump --format=custom)"
run_to "$work/database.dump" "${compose[@]}" exec -T postgres \
  pg_dump -U "$pg_user" -d "$pg_db" --format=custom --compress=6

ops_info "Recording applied migrations and row counts"
run_to "$work/migrations.txt" "${compose[@]}" exec -T postgres \
  psql -U "$pg_user" -d "$pg_db" -X -At -v ON_ERROR_STOP=1 -c "$OPS_MIGRATIONS_SQL"
run_to "$work/table-counts.txt" "${compose[@]}" exec -T postgres \
  psql -U "$pg_user" -d "$pg_db" -X -At -v ON_ERROR_STOP=1 -c "$OPS_COUNT_SQL"

# 2. Scan reports, read from a throw-away container that mounts the volume read-only, without network.
ops_info "Archiving volume '$volume' (read-only mount)"
run_to "$work/reports.tar.gz" docker run --rm --network none -v "$volume:/r:ro" --entrypoint tar "$helper" \
  -czf - -C /r .

if [[ "$dry_run" == true ]]; then
  printf '+ write %s/manifest.json and %s/checksums.sha256, then rename to %s\n' "$work" "$work" "$final"
  echo
  ops_secrets_reminder
  exit 0
fi

[[ -s "$work/database.dump" ]] || ops_die "database.dump is empty."
tar -tzf "$work/reports.tar.gz" >/dev/null || ops_die "reports.tar.gz is not a readable tar.gz archive."

# 3. Manifest (all values are plain identifiers, numbers or Git refs; validated before writing JSON).
safe_value() {
  [[ "$1" =~ ^[A-Za-z0-9._:/+@-]*$ ]] || ops_die "Unexpected characters in manifest value '$1'."
}
json_string() {
  printf '"%s"' "$1"
}
json_list() {
  local first=true line
  printf '['
  while IFS= read -r line; do
    [[ -z "$line" ]] && continue
    [[ "$first" == true ]] || printf ', '
    json_string "$line"
    first=false
  done < "$1"
  printf ']'
}

git_commit="unknown"
git_describe="unknown"
if command -v git >/dev/null 2>&1 && git rev-parse --git-dir >/dev/null 2>&1; then
  git_commit="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
  git_describe="$(git describe --tags --always --dirty 2>/dev/null || echo unknown)"
fi
server_version="$("${compose[@]}" exec -T postgres psql -U "$pg_user" -d "$pg_db" -X -At -c 'SHOW server_version' | awk '{print $1}')"
tables="$(grep -c '|' "$work/table-counts.txt" || true)"
for value in "$project" "$git_commit" "$git_describe" "$server_version" "$pg_db"; do safe_value "$value"; done
while IFS= read -r value; do safe_value "$value"; done < "$work/migrations.txt"
rows="$(awk -F'|' '{sum += $2} END {print sum + 0}' "$work/table-counts.txt")"

{
  printf '{\n'
  printf '  "format": 1,\n'
  printf '  "created_at_utc": %s,\n' "$(json_string "$(date -u +%Y-%m-%dT%H:%M:%SZ)")"
  printf '  "compose_project": %s,\n' "$(json_string "$project")"
  printf '  "mode": %s,\n' "$(json_string "$([[ "$prod" == true ]] && echo production || echo development)")"
  printf '  "aegisops": {"commit": %s, "describe": %s},\n' "$(json_string "$git_commit")" "$(json_string "$git_describe")"
  printf '  "postgres": {"server_version": %s, "database": %s},\n' "$(json_string "$server_version")" "$(json_string "$pg_db")"
  printf '  "migrations": %s,\n' "$(json_list "$work/migrations.txt")"
  printf '  "row_counts": {"tables": %s, "rows": %s, "file": "table-counts.txt"},\n' "$tables" "$rows"
  printf '  "files": {\n'
  printf '    "database.dump": %s,\n' "$(ops_file_size "$work/database.dump")"
  printf '    "reports.tar.gz": %s,\n' "$(ops_file_size "$work/reports.tar.gz")"
  printf '    "migrations.txt": %s,\n' "$(ops_file_size "$work/migrations.txt")"
  printf '    "table-counts.txt": %s\n' "$(ops_file_size "$work/table-counts.txt")"
  printf '  },\n'
  printf '  "not_included": [".env (secrets and encryption keys)", "ollama models", "redis (cache only)"]\n'
  printf '}\n'
} > "$work/manifest.json"

(cd "$work" && ops_sha256 database.dump reports.tar.gz migrations.txt table-counts.txt manifest.json > checksums.sha256)
mv "$work" "$final"
completed=true

echo
ops_info "Backup complete: $final"
(cd "$final" && ls -l)
echo "Migrations: $(grep -c . "$final/migrations.txt" || true); tables: $tables; rows: $rows."
echo "Copy the whole folder off this host (see docs/operations.md: off-host storage and encryption)."
echo
ops_secrets_reminder
