#!/usr/bin/env bash
# P8-16: time a fresh clone to a working stack, fully isolated from any AegisOps stack already running.
#
# Clones the repository into a temporary directory, generates new secrets there, and starts it as the Compose
# project `aegisops-fresh` on its own network and ports (API 127.0.0.1:18080, web 127.0.0.1:13000; nothing else
# published). It then checks the seeded demo data and runs the noop deployment smoke test, and prints the time
# for each phase. Passwords of the throwaway stack are read from its log into variables and never printed.
# The fresh stack never deploys to Docker targets, so existing *-staging containers are not touched.
#
# Usage: scripts/fresh-clone-check.sh [git-url] [branch]   (defaults: this repo's origin, current branch)
#   KEEP=1 keeps the stack and clone afterwards; by default both are removed.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
url="${1:-$(git -C "$here" remote get-url origin)}"
branch="${2:-$(git -C "$here" branch --show-current)}"
project="aegisops-fresh"
work="$(mktemp -d -t aegisops-fresh)"
start=$(date +%s)
phase() { printf '[%4ss] %s\n' "$(( $(date +%s) - start ))" "$*"; }

cleanup() {
  if [[ "${KEEP:-0}" != 1 ]]; then
    (cd "$work/repo" 2>/dev/null && docker compose -p "$project" -f docker-compose.yml -f fresh.override.yml down -v --remove-orphans >/dev/null 2>&1) || true
    rm -rf "$work"
  else
    echo "Kept: $work (Compose project $project)"
  fi
}
trap cleanup EXIT

phase "cloning $url ($branch)"
git clone --quiet --depth 1 --branch "$branch" "$url" "$work/repo"
cd "$work/repo"
scripts/gen-secrets.sh >/dev/null

# Isolation: own network name, no host ports except the API and web on loopback-only alternative ports.
cat > fresh.override.yml <<'YAML'
services:
  postgres:
    ports: !reset []
  redis:
    ports: !reset []
  api:
    ports: !override
      - "127.0.0.1:18080:8080"
  web:
    ports: !override
      - "127.0.0.1:13000:80"
networks:
  aegisops:
    name: aegisops-fresh
YAML
compose=(docker compose -p "$project" -f docker-compose.yml -f fresh.override.yml)

phase "building and starting the stack"
"${compose[@]}" up -d --build --wait postgres redis >/dev/null
"${compose[@]}" up -d --build api worker web >/dev/null
for _ in $(seq 1 120); do
  curl -fsS -o /dev/null http://127.0.0.1:18080/health/live && break
  sleep 2
done
curl -fsS -o /dev/null http://127.0.0.1:18080/health/live || { echo "API did not start" >&2; exit 1; }
curl -fsS http://127.0.0.1:13000/ | grep -q 'id="root"' || { echo "Web did not start" >&2; exit 1; }
phase "API and web are up"

# The seed prints each demo password once; read the admin one without echoing it.
admin_password=""
for _ in $(seq 1 30); do
  admin_password="$("${compose[@]}" logs api 2>/dev/null | sed -n 's/.*Development user "\{0,1\}admin@aegisops.local"\{0,1\} created\. Password: "\{0,1\}\([^" ]*\).*/\1/p' | head -1)"
  [[ -n "$admin_password" ]] && break
  sleep 2
done
[[ -n "$admin_password" ]] || { echo "Seeded admin password not found in the API log" >&2; exit 1; }

token="$(curl -fsS http://127.0.0.1:18080/api/v1/auth/login -H 'Content-Type: application/json' \
  -d "$(jq -n --arg p "$admin_password" '{email:"admin@aegisops.local",password:$p}')" | jq -er .accessToken)"
projects="$(curl -fsS -H "Authorization: Bearer $token" http://127.0.0.1:18080/api/v1/projects | jq -r '[.[].slug] | sort | join(",")')"
[[ "$projects" == "circuit-service,notification-service,site-service" ]] || { echo "Unexpected seeded projects: $projects" >&2; exit 1; }
phase "seeded demo projects present: $projects"

AEGISOPS_URL=http://127.0.0.1:13000 AEGISOPS_ADMIN_PASSWORD="$admin_password" scripts/smoke-compose-deployment.sh >/dev/null
phase "noop deployment smoke passed (request → policy → Worker → Succeeded)"
echo "[PASS] fresh clone to a working, seeded stack in $(( $(date +%s) - start )) s"
