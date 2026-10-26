"""Check that queued scan paths are readable across API/worker container boundaries."""
import json
import os
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[1]
env = dict(os.environ, JWT_SIGNING_KEY="contract-test-only-not-a-real-secret-0000")
compose = json.loads(subprocess.check_output(
    ["docker", "compose", "-f", str(root / "docker-compose.yml"), "config", "--format", "json"],
    cwd=root, env=env, text=True,
))

def volume(service, target):
    return next(m for m in compose["services"][service]["volumes"] if m["target"] == target)

api = volume("api", "/app/reports")
worker = volume("worker", "/app/reports")
assert api["type"] == worker["type"] == "volume"
assert api["source"] == worker["source"]
assert compose["services"]["api"]["environment"]["Storage__ReportsPath"] == "/app/reports"
assert volume("postgres", "/var/lib/postgresql/data")["source"] != api["source"]
print("PASS: shared persistent report store, distinct from existing PostgreSQL volume")
