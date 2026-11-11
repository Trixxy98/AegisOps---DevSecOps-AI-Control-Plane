#!/usr/bin/env bash
# P8-03: show the auth rate limit holding under a concurrent burst against the running API.
#
# Sends N concurrent POST /api/v1/auth/refresh requests WITHOUT a refresh cookie. Such requests are rejected
# with 401 before any database or audit write, but they still count against the `auth` fixed-window limiter
# (10 per minute per client IP, docs/09). Expected: exactly 10 answers that are not 429, the rest 429, and
# fast rejections. No credentials are used and no data changes.
#
# Side effect: for up to one minute afterwards, sign-in from this machine's IP is also rate limited.
#
# Usage: scripts/rate-limit-check.sh [requests] [concurrency]   (defaults: 60 20)
#   AEGISOPS_URL default http://localhost:8080
set -euo pipefail
export AEGISOPS_URL="${AEGISOPS_URL:-http://localhost:8080}"
export BURST="${1:-60}" CONCURRENCY="${2:-20}"
exec python3 -I - <<'PY'
import os, statistics, time, urllib.error, urllib.request
from concurrent.futures import ThreadPoolExecutor

url = os.environ["AEGISOPS_URL"].rstrip("/") + "/api/v1/auth/refresh"
burst, workers = int(os.environ["BURST"]), int(os.environ["CONCURRENCY"])

def hit(_):
    request = urllib.request.Request(url, method="POST", data=b"")
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            status = response.status
    except urllib.error.HTTPError as error:
        status = error.code
    return status, (time.perf_counter() - started) * 1000

wall = time.perf_counter()
with ThreadPoolExecutor(max_workers=workers) as pool:
    results = list(pool.map(hit, range(burst)))
wall = (time.perf_counter() - wall) * 1000

counts = {}
for status, _ in results:
    counts[status] = counts.get(status, 0) + 1
allowed = sum(n for s, n in counts.items() if s != 429)
limited = counts.get(429, 0)

def pct(values, p):
    values = sorted(values)
    return values[min(len(values) - 1, int(round(p / 100 * (len(values) - 1))))] if values else 0

lat_all = [ms for _, ms in results]
lat_429 = [ms for s, ms in results if s == 429]
print(f"{burst} requests, concurrency {workers}, {wall:.0f} ms wall time, {burst / (wall / 1000):.0f} req/s")
print("status counts: " + ", ".join(f"{s}×{n}" for s, n in sorted(counts.items())))
print(f"latency p50 {statistics.median(lat_all):.1f} ms, p95 {pct(lat_all, 95):.1f} ms; "
      f"429 p95 {pct(lat_429, 95):.1f} ms")
ok = allowed <= 10 and limited == burst - allowed and limited > 0 and set(counts) <= {401, 429}
print(f"[{'PASS' if ok else 'FAIL'}] at most 10 requests per minute pass the auth limiter; the rest get 429")
raise SystemExit(0 if ok else 1)
PY
