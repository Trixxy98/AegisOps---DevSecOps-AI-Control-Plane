"""Smoke-test security headers from the running Compose web service."""
from urllib.request import urlopen

with urlopen("http://127.0.0.1:3000/", timeout=10) as response:
    assert response.status == 200
    policy = response.headers.get("Content-Security-Policy", "")
    assert "default-src 'self'" in policy, policy
    assert "script-src 'self'" in policy, policy
    assert "object-src 'none'" in policy, policy
    assert "frame-ancestors 'none'" in policy, policy
    assert "connect-src 'self' ws://127.0.0.1:3000 wss://127.0.0.1:3000" in policy, policy
    assert "'unsafe-inline'" not in policy, policy
    assert "'unsafe-eval'" not in policy, policy

print("PASS: live web CSP restricts scripts, frames and connections")
