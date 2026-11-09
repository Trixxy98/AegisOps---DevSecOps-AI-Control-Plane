#!/usr/bin/env bash
# Stop the infra-only services started by scripts/dev-up.sh. Volumes are kept.
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose stop postgres redis
