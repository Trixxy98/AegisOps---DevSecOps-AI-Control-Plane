#!/usr/bin/env bash
# Journey C (docs/01 §7): governed AI usage against the running stack and a real Ollama model.
#
# Signs in as the seeded admin, syncs Ollama models, allows the model in the Global AI policy,
# sends a prompt containing a FAKE AWS access key with the policy on Redact and then on Block,
# lists the governed request log, asks for an AI deployment summary, and finally restores the
# Global AI policy exactly as it was. Prints only outcomes: never tokens or passwords.
#
# Usage: AEGISOPS_ADMIN_PASSWORD='...' scripts/journey-c.sh   (or omit it to be prompted)
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
import json, os, sys, urllib.error, urllib.request

BASE, MODEL = os.environ["AEGISOPS_URL"].rstrip("/"), os.environ["AEGISOPS_MODEL"]
# Synthetic, well-formed but fake key: AKIA + 16 placeholder characters. Never a real credential.
FAKE_KEY = "AKIA" + "EXAMPLEFAKE12345"
PROMPT = f"My deploy failed with this trace:\n  botocore.exceptions.ClientError (key {FAKE_KEY})\nWhat should I check first?"
token = None

def call(method, path, body=None):
    request = urllib.request.Request(BASE + path, method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})})
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
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

status, body = call("POST", "/api/v1/admin/ai/models/sync")
step("sync Ollama models", status == 200, f"HTTP {status} {body}")

status, policies = call("GET", "/api/v1/ai/policies")
policy = next((p for p in (policies or []) if p["scope"] == "Global" and p["isEnabled"]), None)
if not step("Global AI policy present", policy is not None):
    sys.exit(1)
original = {
    "id": policy["id"], "name": policy["name"], "scope": 1, "scopeId": None,
    "allowedModelIds": json.loads(policy["allowedModelIdsJson"]),
    "requestsPerMinute": policy["requestsPerMinute"], "requestsPerDay": policy["requestsPerDay"],
    "maxPromptChars": policy["maxPromptChars"],
    "sensitiveDataAction": {"Allow": 0, "Redact": 1, "Block": 2}[policy["sensitiveDataAction"]],
    "blockedPatterns": json.loads(policy["blockedPatternsJson"]), "redactPii": policy["redactPii"],
    "storeContent": policy["storeContent"], "isEnabled": policy["isEnabled"],
}

def save(action):
    status, _ = call("PUT", "/api/v1/ai/policies", {**original, "sensitiveDataAction": action})
    return status

try:
    status, models = call("GET", "/api/v1/ai/models")
    model = next((m for m in (models or []) if m["name"] == MODEL), None)
    if model is None:
        print(f"[INFO] {MODEL} is not in the Global allow-list yet; Journey C needs it allowed.")
        print("       Allow it once in AI Governance → AI policy, then re-run this script.")
        sys.exit(1)
    step(f"{MODEL} allowed by the Global policy", True)

    step("policy → Redact", save(1) == 200)
    status, chat = call("POST", "/api/v1/ai/chat", {"model": MODEL, "messages": [{"role": "user", "content": PROMPT}]})
    governance = (chat or {}).get("governance", {}).get("sensitiveData", {})
    step("redacted prompt answered by the real model",
         status == 200 and "AWS_ACCESS_KEY" in (governance.get("detected") or []) and governance.get("redactions", 0) > 0,
         f"HTTP {status}, detected={governance.get('detected')}, redactions={governance.get('redactions')}, "
         f"latencyMs={(chat or {}).get('usage', {}).get('latencyMs')}, tokens={(chat or {}).get('usage', {}).get('completionTokens')}")
    reply = ((chat or {}).get("message") or {}).get("content") or ""
    step("reply never echoes the fake key", FAKE_KEY not in reply)

    step("policy → Block", save(2) == 200)
    status, problem = call("POST", "/api/v1/ai/chat", {"model": MODEL, "messages": [{"role": "user", "content": PROMPT}]})
    step("same prompt blocked before the model is called", status == 422 and (problem or {}).get("reason") == "SensitiveData",
         f"HTTP {status}, reason={(problem or {}).get('reason')}")
finally:
    step("Global AI policy restored", save(original["sensitiveDataAction"]) == 200)

status, log = call("GET", "/api/v1/ai/requests?pageSize=20")
items = [i for i in (log or {}).get("items", []) if "AWS_ACCESS_KEY" in (i.get("detectedSensitiveTypesJson") or "")][:2]
step("AI request log shows both governed requests",
     {i["status"] for i in items} >= {"Completed", "Blocked"} and all(FAKE_KEY not in (i.get("promptPreview") or "") for i in items),
     "; ".join(f"{i['status']}/{i.get('blockReason') or '-'} redactions={i['redactionsApplied']} preview={(i.get('promptPreview') or '')[:60]!r}" for i in items))

status, deployments = call("GET", "/api/v1/deployments")
target = next(iter(deployments or []), None)
if target:
    status, summary = call("POST", f"/api/v1/deployments/{target['id']}/ai-summary", {"model": MODEL})
    text = json.dumps(summary or {})
    step("approver AI deployment summary", status == 200, f"HTTP {status}, {len(text)} chars")
else:
    print("[INFO] no deployments yet; skipped the AI deployment summary")
PY
