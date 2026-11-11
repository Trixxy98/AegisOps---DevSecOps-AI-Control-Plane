#!/usr/bin/env bash
# Start only PostgreSQL and Redis for local `dotnet run` of the API/Worker.
# Stop them again with scripts/dev-down.sh.
set -euo pipefail
cd "$(dirname "$0")/.."
docker compose up -d --wait postgres redis
echo "PostgreSQL: localhost:5432  Redis: localhost:${AEGISOPS_REDIS_PORT:-6380}"
echo "Run the API:    dotnet run --project src/AegisOps.Api"
echo "Run the Worker: dotnet run --project src/AegisOps.Worker"
