"""Check that Staging Worker Docker access does not change persistent stores or API privilege."""
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

def mount(service, target):
    return next(m for m in compose["services"][service]["volumes"] if m["target"] == target)

worker = compose["services"]["worker"]
socket = mount("worker", "/var/run/docker.sock")
assert socket["type"] == "bind" and socket["source"] == "/var/run/docker.sock"
assert mount("api", "/app/reports")["source"] == mount("worker", "/app/reports")["source"]
assert mount("postgres", "/var/lib/postgresql/data")["source"] != mount("worker", "/app/reports")["source"]
assert all(m["target"] != "/var/run/docker.sock" for m in compose["services"]["api"]["volumes"])
assert worker["environment"]["DOCKER_HEALTH_HOST"] == "host.docker.internal"
assert worker["networks"] == {"aegisops": None}
print("PASS: Worker-only Docker socket, host health route, persistent stores unchanged")
