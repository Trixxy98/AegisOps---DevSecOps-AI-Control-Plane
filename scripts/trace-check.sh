#!/usr/bin/env bash
# P7-03: list which AegisOps spans Jaeger has received from the API and the Worker (read-only).
# Run after some activity (a CI register + Staging deployment, an approval, an AI request) with
# deploy/docker-compose.tracing.yml enabled.
#
# Usage: scripts/trace-check.sh [lookback, e.g. 1h]   JAEGER_URL default http://127.0.0.1:16686
set -euo pipefail
export JAEGER_URL="${JAEGER_URL:-http://127.0.0.1:16686}" LOOKBACK="${1:-1h}"
exec python3 -I - <<'PY'
import json, os, urllib.parse, urllib.request

base, lookback = os.environ["JAEGER_URL"].rstrip("/"), os.environ["LOOKBACK"]
EXPECTED = {
    "use cases": ["Api.Request", "RequestDeployment", "EvaluateDeployment", "ExecuteDeployment"],
    "policy": ["PolicyEngine.Evaluate"],
    "scans": ["Scanner.ParseReport"],
    "Ollama": ["Ollama.Chat"],
    "Docker execution": ["Docker.Execute", "Docker.PullImage", "Docker.CreateContainer", "Docker.StartContainer", "Docker.HealthCheck"],
}

def get(path):
    with urllib.request.urlopen(base + path, timeout=15) as response:
        return json.load(response)

seen, api_traces, links = {}, set(), set()
for service in ("aegisops-api", "aegisops-worker"):
    query = urllib.parse.urlencode({"service": service, "lookback": lookback, "limit": 500})
    for trace in get(f"/api/traces?{query}").get("data", []):
        for span in trace["spans"]:
            service_name = trace["processes"][span["processID"]]["serviceName"]
            seen.setdefault(span["operationName"], set()).add(service_name)
            if service_name == "aegisops-api":
                api_traces.add(span["traceID"])
            # Worker jobs start their own trace and link (FOLLOWS_FROM) to the API trace that enqueued them.
            for ref in span.get("references", []):
                if service_name == "aegisops-worker" and ref["refType"] == "FOLLOWS_FROM":
                    links.add((span["traceID"], ref["traceID"]))

all_ok = True
for area, names in EXPECTED.items():
    found = [n for n in names if n in seen]
    ok = bool(found)
    all_ok &= ok
    detail = ", ".join(f"{n} ({'/'.join(sorted(seen[n]))})" for n in found) or "none yet"
    print(f"[{'PASS' if ok else 'MISS'}] {area}: {detail}")
linked = sorted({worker for worker, api in links if api in api_traces})
print(f"[{'PASS' if linked else 'MISS'}] Worker job traces linked to the API trace that enqueued them: {len(linked)}")
print(f"{len(seen)} distinct span names received in the last {lookback}.")
raise SystemExit(0 if all_ok and linked else 1)
PY
