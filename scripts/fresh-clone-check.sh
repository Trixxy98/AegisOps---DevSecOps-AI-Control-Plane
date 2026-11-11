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
#   NO_CACHE=1 builds the images without Docker's layer cache (a cold build, as on a new machine).
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

if [[ "${NO_CACHE:-0}" == 1 ]]; then
  # Cold build: no layer cache and fresh base images, as on a new machine.
  phase "building images without cache (NO_CACHE=1)"
  "${compose[@]}" build --no-cache --pull api worker web >/dev/null
fi
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

login() {
  curl -fsS http://127.0.0.1:18080/api/v1/auth/login -H 'Content-Type: application/json' \
    -d "$(jq -n --arg p "$1" '{email:"admin@aegisops.local",password:$p}')" | jq -er .accessToken
}
# First sign-in: the seeded password is temporary, so replace it (as the web UI would) with a random one.
temporary_token="$(login "$admin_password")"
new_password="$(openssl rand -base64 24 | tr -d '\n/+=')"
curl -fsS -o /dev/null -X POST http://127.0.0.1:18080/api/v1/auth/change-password \
  -H "Authorization: Bearer $temporary_token" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg c "$admin_password" --arg n "$new_password" '{currentPassword:$c,newPassword:$n}')"
admin_password="$new_password"
token="$(login "$admin_password")"
phase "first sign-in: temporary admin password replaced"
projects="$(curl -fsS -H "Authorization: Bearer $token" http://127.0.0.1:18080/api/v1/projects | jq -r '[.[].slug] | sort | join(",")')"
[[ "$projects" == "circuit-service,notification-service,site-service" ]] || { echo "Unexpected seeded projects: $projects" >&2; exit 1; }
phase "seeded demo projects present: $projects"

AEGISOPS_URL=http://127.0.0.1:13000 AEGISOPS_ADMIN_PASSWORD="$admin_password" scripts/smoke-compose-deployment.sh >/dev/null
phase "noop deployment smoke passed (request → policy → Worker → Succeeded)"
echo "[PASS] fresh clone to a working, seeded stack in $(( $(date +%s) - start )) s"
