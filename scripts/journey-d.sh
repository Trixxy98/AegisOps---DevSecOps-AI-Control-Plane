#!/usr/bin/env bash
# Journey D step 3 (docs/demo-script.md), read-only: shows that the Production baseline's deployment
# window denies a Friday 20:00 MYT request and allows one inside the window, using the policy simulator
# (no deployment is created). Also prints the Production baseline version and its policy.updated audit rows.
# Prints only outcomes: never tokens or passwords.
#
# Usage: AEGISOPS_ADMIN_PASSWORD='...' scripts/journey-d.sh [project-slug]   (default: circuit-service)
#   AEGISOPS_URL default http://localhost:8080
set -euo pipefail
export AEGISOPS_URL="${AEGISOPS_URL:-http://localhost:8080}"
export AEGISOPS_PROJECT="${1:-circuit-service}"
if [[ -z "${AEGISOPS_ADMIN_PASSWORD:-}" ]]; then
  read -r -s -p "Password for admin@aegisops.local: " AEGISOPS_ADMIN_PASSWORD; echo
fi
export AEGISOPS_ADMIN_PASSWORD
exec python3 -I - <<'PY'
import json, os, sys, urllib.error, urllib.request
from datetime import datetime, timedelta, timezone

BASE, SLUG = os.environ["AEGISOPS_URL"].rstrip("/"), os.environ["AEGISOPS_PROJECT"]
MYT = timezone(timedelta(hours=8))
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

def step(name, ok, detail=""):
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))
    return ok

status, body = call("POST", "/api/v1/auth/login",
    {"email": "admin@aegisops.local", "password": os.environ["AEGISOPS_ADMIN_PASSWORD"]})
if not step("admin sign-in", status == 200 and body and body.get("accessToken"), f"HTTP {status}"):
    sys.exit(1)
token = body["accessToken"]
_, me = call("GET", "/api/v1/auth/me")
if (me or {}).get("mustChangePassword"):
    print("[FAIL] this account still has a temporary password: sign in at the web UI once and choose a new one, then re-run")
    sys.exit(1)

_, policies = call("GET", "/api/v1/policies")
baseline = next((p for p in policies or [] if p.get("name") == "Production baseline"), None)
step("Production baseline policy present", baseline is not None,
     f"version {baseline.get('version')}" if baseline else "")

_, project = call("GET", f"/api/v1/projects/{SLUG}")
production = next((e for e in (project or {}).get("environments", []) if e["tier"] == "Production"), None)
_, artifacts = call("GET", f"/api/v1/projects/{SLUG}/artifacts?branch=main")
items = artifacts.get("items", artifacts) if isinstance(artifacts, dict) else (artifacts or [])
artifact = next((a for a in items if a.get("branch") == "main"), None)
if not step(f"{SLUG}: main artifact and Production environment", production is not None and artifact is not None,
            artifact.get("version") if artifact else ""):
    sys.exit(1)

now = datetime.now(MYT)
friday = (now + timedelta(days=(4 - now.weekday()) % 7)).replace(hour=20, minute=0, second=0, microsecond=0)
if friday <= now:
    friday += timedelta(days=7)
wednesday = (friday - timedelta(days=2)).replace(hour=10)

def simulate(at):
    status, result = call("POST", "/api/v1/policies/simulate",
        {"artifactId": artifact["id"], "environmentId": production["id"], "at": at.isoformat()})
    window = next((r for r in (result or {}).get("results", []) if r.get("type") == "DeploymentWindow"), {})
    return status, (result or {}).get("decision"), window

status, decision, window = simulate(friday)
step(f"request at {friday:%a %Y-%m-%d %H:%M} MYT is denied by the window",
     status == 200 and decision == "Deny" and window and not window.get("passed"),
     f"HTTP {status}, decision={decision}, rule: {window.get('message')}")
status, decision, window = simulate(wednesday)
step(f"request at {wednesday:%a %Y-%m-%d %H:%M} MYT passes the window",
     status == 200 and window and window.get("passed"), f"decision={decision}, rule: {window.get('message')}")

_, audit = call("GET", "/api/v1/audit")
updates = [a for a in audit or [] if a.get("action") == "policy.updated" and baseline and a.get("resourceId") == baseline.get("id")]
step("Production baseline edits are in the audit log", len(updates) > 0,
     "; ".join(f"{a.get('timestamp', '')[:19]} by {a.get('actorDisplay')}" for a in updates[:3]))
PY
