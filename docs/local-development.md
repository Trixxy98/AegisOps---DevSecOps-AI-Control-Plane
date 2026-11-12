# Local development and evaluation

The default Compose stack for evaluation, learning and demos on one machine (moved from the README).
For a team server with HTTPS use [deploy-production.md](deploy-production.md).

## Quick start

> **New to DevSecOps?** Start with the [user manual](user-manual.md): it explains the ideas in plain
> words, then walks you through installation and four hands-on tutorials.
> **Want to deploy your own application?** Follow [connect your repository](connect-your-repository.md).

Requires Docker Compose, about 4 GB of free memory and ~5 GB of disk. A fresh clone is up in about a minute on
an Apple M-series laptop (images already cached: ~30 s; cold build: ~50 s, plus base-image downloads).

```bash
scripts/gen-secrets.sh            # writes a mode-600 .env with random keys (never commit it)
docker compose up --build -d      # API http://127.0.0.1:8080, web http://127.0.0.1:3000
docker compose logs api | grep 'Development user'   # first-run passwords, printed once
```

Open <http://127.0.0.1:3000> and sign in as `admin@aegisops.local`. **Every seeded account must choose its own
password at first sign-in** (12+ characters); until then the API refuses everything except changing it. Lost a
first-run password? An Admin can issue a new temporary one (`scripts/demo-reset-passwords.sh`), or start over
with `scripts/reset-demo.sh`. Then follow [docs/demo-script.md](demo-script.md) for the four journeys.

Smoke-test the core loop (after the admin password is changed):

```bash
AEGISOPS_ADMIN_PASSWORD='<your admin password>' scripts/smoke-compose-deployment.sh
```

The smoke test uses the Web proxy to sign in, create a project/artifact, request a
Development deployment, and assert the worker recorded an Allow evaluation and
Succeeded event and updated the environment's current artifact. Each run creates a
uniquely named test project in the local database. If the database already exists, the
seed password is not regenerated: keep the first-run password or reset the local database deliberately.
The API is also exposed at `http://localhost:8080`; Web is at `http://localhost:3000`.

## Optional Prometheus and Grafana

Set `GRAFANA_ADMIN_PASSWORD` in your shell or the gitignored `.env` file before
starting the opt-in overlay; there is no checked-in/default Grafana password.
The base Compose stack does not require this variable. With a working Docker daemon:

```bash
docker compose -f docker-compose.yml -f deploy/docker-compose.observability.yml \
  --profile observability up --build -d
```

Open Grafana at `http://127.0.0.1:3001` (user `admin`, password from your
configuration) and the Prometheus targets/rules pages at `http://127.0.0.1:9090`.
Only these UI ports bind to host loopback; Prometheus scrapes API `:9464` and
Worker `:8081` internally. The provisioned **AegisOps Overview** dashboard
shows API p95/5xx and Worker queue/throughput; additional Delivery/AI panels
need instruments that are not yet built. Prometheus rules do not send notifications
by default. To stop only monitoring, use the same Compose flags followed by
`stop grafana prometheus`; do not run `down -v` because it removes persistent data.
See [`docs/15-observability.md`](15-observability.md) for metric semantics.

This path uses the **noop** executor; it does not start a target container. For the
existing `site-service` project only, the Development seed changes its **Staging**
environment from noop to Docker (`site-service-staging`, network `aegisops-staging`,
host port 8101, container port 8080, `/health`). It never changes Production or an
operator-configured target. Staging requires three fresh security scans and passing
tests. Register a real CI artifact from `ghcr.io/trixxy98/site-service` before
requesting Staging; `ghcr.io/aegisops/site-service:latest` in the design-only
`deploy/docker-compose.targets.yml` is not a published image. That file is **not**
part of this Compose startup; do not start its Production services. The target image
currently publishes linux/amd64 only; the executor retries with that platform when
the default pull fails on arm64.

The Worker passes the target environment name to new containers as both
`DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT`. `site-service` reads this via
ASP.NET for `/version`; an already-running Staging container will continue to say
Production until a new authorized Staging deployment replaces it. This does not
modify running containers or the Production target.

Compose mounts the host Docker socket **only into the Worker** so it can manage
containers. `DOCKER_HEALTH_HOST=host.docker.internal` lets the Worker probe host
published port 8101 (its own loopback is not the Docker host). This assumes Docker
Desktop; another Docker host needs an operator-configured reachable address. The
API does not have socket access. Its Docker runtime endpoint reports
`inspectionAvailable: false` and `containers: null` when no Docker socket exists,
rather than returning 503 or pretending that no containers are running. The
deployment timeline and host `/health` and `/version` remain usable. Other Docker
errors can still return 503; no socket access is added to the API.
The Docker socket grants effective root-equivalent control over the host Docker daemon
and all containers/volumes, despite being mounted only in the Worker. Run this
Development stack only on a trusted local machine, restrict access to AegisOps
deployment APIs, and never use this pattern in Production without isolating the daemon
or introducing a constrained Docker authorization proxy. The executor refuses to
replace an unlabeled/foreign named container or remove an existing `-previous` backup;
verify target names and port 8101 are free before requesting a deployment. It does not
auto-deploy during Compose startup. Check wiring with
`python3 tests/staging-docker-contract.py`. Do not point this stack at production.
For the controlled upgrade and failed-health rollback exercise, see
[`docs/staging-rollback-validation.md`](staging-rollback-validation.md).

SARIF uploads larger than 2 MB are queued for parsing by the worker. Compose mounts a
dedicated persistent `reports` volume at `/app/reports` in both API and worker, separate
from `pgdata`. Before upgrading an existing stack, preserve any pending reports from the
old API container's `/app/reports` (or re-upload those reports after upgrading); an
existing container's private filesystem is **not** migrated automatically into the new
volume. Do not run `docker compose down -v`: it deletes the database and reports volumes.
Check the mount wiring with `python3 tests/report-storage-contract.py`.

## Optional local AI model (Ollama) and hardware

The AI gateway talks to Ollama at `http://ollama:11434/`. Ollama is opt-in:

```bash
docker compose --profile ai up -d   # starts ollama, then ollama-init pulls the model once
OLLAMA_MODEL=qwen2.5-coder:7b docker compose --profile ai up -d ollama-init   # optional second model
```

| Model | Download | Practical minimum | Notes |
| --- | --- | --- | --- |
| `llama3.2:3b` (default) | ~2 GB | 8 GB RAM, 4 CPU cores, no GPU | Chat, deployment summaries; expect a few seconds per reply on CPU |
| `qwen2.5-coder:7b` (optional) | ~4.7 GB | 16 GB RAM; GPU or Apple Silicon recommended | Better finding explanations; slow on CPU-only laptops |

- Leave at least 4 GB free for PostgreSQL, Redis, the API/Worker and the browser on top
  of the model's memory. Models are kept in the `ollama` volume, so pulls happen once.
- Docker Desktop on macOS runs Ollama in a Linux VM **without** Metal GPU access, so it
  is CPU-only. For faster replies, run native Ollama on the host (optionally with
  `OLLAMA_MODELS` on an external disk) and add `-f deploy/docker-compose.host-ollama.yml`,
  which points the API at `http://host.docker.internal:11434/`. On an Apple M4, a
  `llama3.2:3b` reply took about 2 s this way.
- NVIDIA GPUs on Linux need the NVIDIA Container Toolkit and a `gpus: all` override for
  the `ollama` service.
- Without Ollama the platform still runs: readiness reports Ollama as Degraded, and AI
  requests fail with `ProviderUnavailable` (sensitive-data blocks still apply first).
