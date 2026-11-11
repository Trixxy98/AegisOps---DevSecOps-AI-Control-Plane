#!/usr/bin/env bash
# Put the seeded demo users into the team that owns the demo projects, so Journey A works as
# docs/demo-script.md describes: developer@ requests (team Owner), security@ and approver@ approve
# (team Members). Uses PUT /api/v1/teams/{slug}/members/{userId} (idempotent, audited).
# Prints only outcomes: never tokens or passwords.
#
# Usage: AEGISOPS_ADMIN_PASSWORD='...' scripts/demo-team-members.sh [team-slug]   (default: edge-ops)
#   AEGISOPS_URL default http://localhost:8080
set -euo pipefail
export AEGISOPS_URL="${AEGISOPS_URL:-http://localhost:8080}"
export AEGISOPS_TEAM="${1:-edge-ops}"
if [[ -z "${AEGISOPS_ADMIN_PASSWORD:-}" ]]; then
  read -r -s -p "Password for admin@aegisops.local: " AEGISOPS_ADMIN_PASSWORD; echo
fi
export AEGISOPS_ADMIN_PASSWORD
exec python3 -I - <<'PY'
import json, os, sys, urllib.error, urllib.request

BASE, TEAM = os.environ["AEGISOPS_URL"].rstrip("/"), os.environ["AEGISOPS_TEAM"]
WANTED = {"developer@aegisops.local": "Owner", "security@aegisops.local": "Member", "approver@aegisops.local": "Member"}
token = None

def call(method, path, body=None):
    request = urllib.request.Request(BASE + path, method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as error:
        return error.code, None

status, body = call("POST", "/api/v1/auth/login",
    {"email": "admin@aegisops.local", "password": os.environ["AEGISOPS_ADMIN_PASSWORD"]})
if status != 200 or not body or not body.get("accessToken"):
    print(f"[FAIL] admin sign-in — HTTP {status}"); sys.exit(1)
token = body["accessToken"]
_, me = call("GET", "/api/v1/auth/me")
if (me or {}).get("mustChangePassword"):
    print("[FAIL] this account still has a temporary password: sign in at the web UI once and choose a new one, then re-run")
    sys.exit(1)
print("[PASS] admin sign-in")

status, users = call("GET", "/api/v1/admin/users")
by_email = {u["email"].lower(): u for u in users or []}
failed = False
for email, role in WANTED.items():
    user = by_email.get(email)
    if user is None:
        print(f"[FAIL] {email} not found"); failed = True; continue
    status, _ = call("PUT", f"/api/v1/teams/{TEAM}/members/{user['id']}", {"role": role})
    ok = 200 <= status < 300
    failed |= not ok
    print(f"[{'PASS' if ok else 'FAIL'}] {email} → {TEAM} as {role} — HTTP {status}")
sys.exit(1 if failed else 0)
PY
