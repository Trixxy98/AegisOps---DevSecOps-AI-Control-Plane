#!/usr/bin/env bash
# P6-13 (docs/08): the AI deployment summary for a Production approver, against the running stack.
#
# Signs in as the seeded admin, picks an artifact that already succeeded in Staging, and SIMULATES the
# Production policy first: it continues only when the decision is RequireApproval, so nothing can be
# executed. It then requests the Production deployment, waits for AwaitingApproval, asks the real model
# for the approver summary, and finally CANCELS the deployment (also on any error). Prints only
# outcomes: never tokens or passwords.
#
# Usage: AEGISOPS_ADMIN_PASSWORD='...' scripts/journey-p6-13.sh   (or omit it to be prompted)
#   AEGISOPS_URL   default http://localhost:8080
#   AEGISOPS_MODEL default llama3.2:3b
set -euo pipefail
export AEGISOPS_URL="${AEGISOPS_URL:-http://localhost:8080}"
export AEGISOPS_MODEL="${AEGISOPS_MODEL:-llama3.2:3b}"
if [[ -z "${AEGISOPS_ADMIN_PASSWORD:-}" ]]; then
  read -r -s -p "Password for admin@aegisops.local: " AEGISOPS_ADMIN_PASSWORD; echo
fi
export AEGISOPS_ADMIN_PASSWORD
exec python3 -I - <<'PY'
import json, os, sys, time, uuid, urllib.error, urllib.request

BASE, MODEL = os.environ["AEGISOPS_URL"].rstrip("/"), os.environ["AEGISOPS_MODEL"]
token = None

def call(method, path, body=None, headers=None):
    request = urllib.request.Request(BASE + path, method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json", **(headers or {}),
                 **({"Authorization": f"Bearer {token}"} if token else {})})
    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as error:
        raw = error.read()
        try: return error.code, json.loads(raw) if raw else None
        except ValueError: return error.code, None

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

# Map every environment to its project and tier.
_, projects = call("GET", "/api/v1/projects")
tiers, production = {}, {}
for project in projects or []:
    _, detail = call("GET", f"/api/v1/projects/{project['slug']}")
    for env in (detail or {}).get("environments", []):
        tiers[env["id"]] = env["tier"]
        if env["tier"] == "Production":
            production[project["id"]] = env
_, deployments = call("GET", "/api/v1/deployments?status=Succeeded")
source = next((d for d in deployments or [] if tiers.get(d["environmentId"]) == "Staging" and d["projectId"] in production), None)
if not step("artifact that succeeded in Staging, with a Production environment", source is not None):
    sys.exit(1)
target = production[source["projectId"]]

# Safety gate: never request unless policy demands approval (so the Worker cannot execute it).
status, simulated = call("POST", "/api/v1/policies/simulate", {"artifactId": source["artifactId"], "environmentId": target["id"]})
decision = (simulated or {}).get("decision")
if not step("Production policy simulation requires approval", status == 200 and decision == "RequireApproval",
            f"HTTP {status}, decision={decision}, approvalsRequired={(simulated or {}).get('approvalsRequired')}"):
    print("[INFO] stopped before requesting anything; nothing was changed.")
    sys.exit(1)

status, created = call("POST", "/api/v1/deployments",
    {"artifactId": source["artifactId"], "environmentId": target["id"], "reason": "P6-13 approver summary check; cancelled afterwards"},
    {"Idempotency-Key": str(uuid.uuid4())})
deployment_id = (created or {}).get("id")
if not step("Production deployment requested", status == 202 and deployment_id, f"HTTP {status}"):
    sys.exit(1)

def current():
    _, rows = call("GET", f"/api/v1/deployments/{deployment_id}")
    return (rows or {}).get("status")

summary = None
try:
    state = None
    for _ in range(120):
        state = current()
        if state not in ("Requested", "Evaluating"): break
        time.sleep(0.5)
    if not step("Worker evaluated it to AwaitingApproval", state == "AwaitingApproval", f"status={state}"):
        sys.exit(1)

    started = time.monotonic()
    status, summary = call("POST", f"/api/v1/deployments/{deployment_id}/ai-summary", {"model": MODEL})
    text = (summary or {}).get("summary") or ""
    usage = (summary or {}).get("usage") or {}
    step("approver AI deployment summary from the real model", status == 200 and len(text) > 0,
         f"HTTP {status}, {len(text)} chars, label={(summary or {}).get('label')!r}, "
         f"latencyMs={usage.get('latencyMs')}, tokens={usage.get('completionTokens')}, wall={time.monotonic() - started:.1f}s")
    if text:
        print("       " + text[:500].replace("\n", "\n       ") + ("…" if len(text) > 500 else ""))
finally:
    status, _ = call("POST", f"/api/v1/deployments/{deployment_id}/cancel", {})
    step("deployment cancelled", current() == "Cancelled", f"HTTP {status}, status={current()}")

request_id = (summary or {}).get("requestId")
status, log = call("GET", "/api/v1/ai/requests?pageSize=20")
entry = next((i for i in (log or {}).get("items", []) if i.get("id") == request_id), {})
step("summary recorded in the AI request log", entry.get("status") == "Completed",
     f"purpose={entry.get('purpose')}, status={entry.get('status')}, tokens={entry.get('completionTokens')}")
PY
