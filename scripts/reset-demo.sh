#!/usr/bin/env bash
# Reset the local demo to a freshly seeded state (docs/14 §7).
#
# Deletes ONLY this Compose project's `pgdata` (database) and `reports` (scan reports)
# volumes, then starts the stack again so the API migrates and re-seeds demo users,
# teams and policies. The `ollama` model volume and Grafana/Prometheus data are kept.
#
# Usage: scripts/reset-demo.sh [--yes] [--dry-run] [--project-name NAME]
#   --yes            skip the typed confirmation (for scripted demos)
#   --dry-run        print the commands without running them
#   --project-name   Compose project to reset (default: the one `docker compose` resolves here)
set -euo pipefail
cd "$(dirname "$0")/.."

assume_yes=false
dry_run=false
project=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) assume_yes=true; shift ;;
    --dry-run) dry_run=true; shift ;;
    --project-name) project="${2:?--project-name needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

compose=(docker compose)
if [[ -n "$project" ]]; then
  compose+=(-p "$project")
else
  project="$(docker compose config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')"
fi
if [[ ! "$project" =~ ^[a-z0-9][a-z0-9_-]*$ ]]; then
  echo "Refusing to reset: unexpected Compose project name '$project'." >&2
  exit 1
fi
volumes=("${project}_pgdata" "${project}_reports")

run() {
  if [[ "$dry_run" == true ]]; then
    printf '+ %s\n' "$*"
  else
    "$@"
  fi
}

echo "This deletes the '$project' demo database and scan reports:"
printf '  - %s\n' "${volumes[@]}"
echo "Demo users get NEW first-run passwords. The Ollama model volume is kept."
if [[ "$assume_yes" != true && "$dry_run" != true ]]; then
  read -r -p "Type 'reset' to continue: " answer
  [[ "$answer" == "reset" ]] || { echo "Aborted; nothing changed."; exit 1; }
fi

# Stop everything that holds the volumes (including opt-in profiles), keep other volumes.
run "${compose[@]}" --profile ai --profile observability down --remove-orphans
for volume in "${volumes[@]}"; do
  if [[ "$dry_run" == true ]] || docker volume inspect "$volume" >/dev/null 2>&1; then
    run docker volume rm "$volume"
  fi
done
run "${compose[@]}" up -d --wait postgres redis
run "${compose[@]}" up -d --build api worker web

echo "Reset complete. New demo passwords (shown once):"
run sh -c "sleep 5; ${compose[*]} logs api | grep 'Development user' || true"
