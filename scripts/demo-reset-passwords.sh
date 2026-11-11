#!/usr/bin/env bash
# Give the demo accounts new temporary passwords through the audited admin endpoint
# (POST /api/v1/admin/users/{id}/reset-password, docs/09). Use it when the first-run
# passwords from the API log are lost. Existing sessions of those users end.
#
# The temporary passwords are printed ONCE to this terminal only. Do not paste them anywhere.
#
# Usage: AEGISOPS_ADMIN_PASSWORD='...' scripts/demo-reset-passwords.sh [email ...]
#   default emails: developer@, security@ and approver@aegisops.local
#   AEGISOPS_URL default http://localhost:8080
set -euo pipefail
export AEGISOPS_URL="${AEGISOPS_URL:-http://localhost:8080}"
if [[ $# -gt 0 ]]; then
  AEGISOPS_EMAILS="$*"
else
  AEGISOPS_EMAILS="developer@aegisops.local security@aegisops.local approver@aegisops.local"
fi
export AEGISOPS_EMAILS
if [[ -z "${AEGISOPS_ADMIN_PASSWORD:-}" ]]; then
  read -r -s -p "Password for admin@aegisops.local: " AEGISOPS_ADMIN_PASSWORD; echo
fi
export AEGISOPS_ADMIN_PASSWORD
exec python3 -I - <<'PY'
import json, os, sys, urllib.error, urllib.request

BASE = os.environ["AEGISOPS_URL"].rstrip("/")
EMAILS = [e.lower() for e in os.environ["AEGISOPS_EMAILS"].split()]
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

_, users = call("GET", "/api/v1/admin/users")
by_email = {u["email"].lower(): u for u in users or []}
failed, created = False, []
for email in EMAILS:
    user = by_email.get(email)
    if user is None:
        print(f"[FAIL] {email} not found"); failed = True; continue
    status, result = call("POST", f"/api/v1/admin/users/{user['id']}/reset-password")
    if status == 200 and result and result.get("temporaryPassword"):
        created.append((email, result["temporaryPassword"]))
        print(f"[PASS] {email} — temporary password set")
    else:
        print(f"[FAIL] {email} — HTTP {status}"); failed = True

if created:
    print("\nTemporary passwords (shown once; keep them private, do not paste them into chats):")
    for email, password in created:
        print(f"  {email:<28} {password}")
sys.exit(1 if failed else 0)
PY
