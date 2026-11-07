"""Validate the opt-in monitoring stack without starting Docker containers."""
import json
import os
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[1]
base = root / "docker-compose.yml"
overlay = root / "deploy/docker-compose.observability.yml"
env = dict(os.environ, JWT_SIGNING_KEY="contract-test-only-not-a-real-secret-0000",
           GRAFANA_ADMIN_PASSWORD="contract-test-only-not-a-real-secret-0000")


def config(*files):
    return json.loads(subprocess.check_output(
        ["docker", "compose", *(part for file in files for part in ("-f", str(file))),
         "--profile", "observability", "config", "--format", "json"],
        cwd=root, env=env, text=True,
    ))


stack = config(base, overlay)
services = stack["services"]
assert {"prometheus", "grafana"} <= services.keys()
for name in ("prometheus", "grafana"):
    assert services[name]["profiles"] == ["observability"]
    assert services[name]["networks"] == {"aegisops": None}
    assert services[name]["ports"][0]["host_ip"] == "127.0.0.1"
for name in ("api", "worker"):
    assert all(port.get("target") not in (9464, 8081) for port in services[name].get("ports", []))
assert any(m["target"] == "/etc/prometheus/prometheus.yml" and m["read_only"]
           for m in services["prometheus"]["volumes"])
assert any(m["target"] == "/etc/prometheus/rules.yml" and m["read_only"]
           for m in services["prometheus"]["volumes"])
assert any(m["target"] == "/etc/grafana/provisioning" and m["read_only"]
           for m in services["grafana"]["volumes"])
assert any(m["target"] == "/var/lib/grafana/dashboards" and m["read_only"]
           for m in services["grafana"]["volumes"])
assert services["grafana"]["environment"]["GF_SECURITY_ADMIN_PASSWORD"] == env["GRAFANA_ADMIN_PASSWORD"]

scrapes = (root / "deploy/prometheus/prometheus.yml").read_text()
assert 'targets: ["api:9464"]' in scrapes
assert 'targets: ["worker:8081"]' in scrapes
assert "api:8080" not in scrapes
assert "url: http://prometheus:9090" in (
    root / "deploy/grafana/provisioning/datasources/prometheus.yml").read_text()
assert "path: /var/lib/grafana/dashboards" in (
    root / "deploy/grafana/provisioning/dashboards/aegisops.yml").read_text()

# Running the default Compose stack must not require Grafana credentials.
base_env = dict(os.environ, JWT_SIGNING_KEY=env["JWT_SIGNING_KEY"])
base_env.pop("GRAFANA_ADMIN_PASSWORD", None)
subprocess.check_call(["docker", "compose", "-f", str(base), "config", "--quiet"],
                      cwd=root, env=base_env)

# Assert all dashboard queries use implemented metrics and respect shared queue semantics.
dashboard = json.loads((root / "deploy/grafana/dashboards/overview.json").read_text())
assert dashboard["uid"] == "aegisops-overview"
panels = dashboard["panels"]
queries = [target["expr"] for panel in panels for target in panel["targets"]]
assert any("http_server_request_duration_seconds_bucket" in query for query in queries)
assert any("http_response_status_code" in query for query in queries)
for panel in panels[:2]:
    for target in panel["targets"]:
        expr = target["expr"]
        assert expr.count("http_server_request_duration_seconds_") == expr.count(
            'http_route!~"/hubs/.*"'
        ), panel["title"]
assert any("max by (status) (aegisops_jobs_queue_depth" in query for query in queries)
assert any("aegisops_jobs_processed_total" in query for query in queries)
assert all(panel["datasource"]["uid"] == "prometheus" for panel in panels)
print("PASS: opt-in monitoring services, private scrape ports, dashboard contract")
