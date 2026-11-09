# Demo script — the four v1 journeys

A presenter's walkthrough of Journeys A–D from
[01 Vision and scope §7](01-vision-and-scope.md). It takes about 15 minutes after the
stack is running. Use only the seeded demo accounts and synthetic data. Never paste real
keys, tokens or customer data into the AI playground.

## 0. Preparation (before the audience joins)

```bash
scripts/gen-secrets.sh                      # first time only; writes .env (mode 600)
docker compose up --build -d                # core stack; the API migrates and seeds
docker compose --profile ai up -d           # Journey C only; first pull takes minutes
docker compose -f deploy/docker-compose.targets.yml up -d   # Staging/Production target networks
docker compose logs api | grep 'Development user'           # first-run passwords
```

Open two browser windows side by side, both at <http://localhost:3000>:

| Window | Account | Roles | Used for |
| --- | --- | --- | --- |
| Left | `developer@aegisops.local` | Developer, team owner | Requesting deployments, AI requests |
| Right | `security@aegisops.local` | Security, Approver | Approvals, policies, AI request log |

`approver@aegisops.local` is the second approver for Production, and
`admin@aegisops.local` can see the audit log and the job queue. The seed creates the
`Production baseline` policy (two approvals, plus `MaxFindings(Critical) = 0` across all
tiers) and Docker Staging targets for `circuit-service` and `site-service` on the
`aegisops-staging` network.

Create a project API key with `artifacts:write`, `scans:write` and
`deployments:request` for each target project (Projects → API keys). Export it only in the
terminal you present from:

```bash
export AEGISOPS_URL=http://localhost:8080 AEGISOPS_API_KEY='<key shown once in the UI>'
```

Optional: open Grafana (`--profile observability`, see README) on the Delivery and
AI Governance dashboards to show the same events as metrics.

## Journey A — A developer ships to production (~5 min)

1. *"A merge to `circuit-service/main` has just built an image and run Gitleaks, Trivy
   and Semgrep."* Replay that CI run:
   `scripts/ci-replay.sh tests/fixtures/ci-payloads/circuit-service.json`.
2. Left window → **Projects** → `circuit-service` → **Artifacts**: the new artifact shows its digest, test summary and three
   scans with 0 critical findings.
3. Left window → **Deployments**: the Staging request evaluated to **Allow**. Open it and
   watch the timeline stream live: *ImagePulled → ContainerStarted → HealthCheckPassed →
   Succeeded*. Point out the rule-by-rule explanation.
4. Left window: request the same artifact to **Production**. The decision is
   **RequireApproval (2)**. The right window shows the approval toast within a second.
5. Right window (Security) approves, then sign in as `approver@aegisops.local` and
   approve again. The Worker re-checks the policy before executing, then deploys.
6. Admin → **Audit log**: request, evaluation, both approvals and execution are all listed
   with one correlation ID.

## Journey B — A vulnerable build is blocked (~3 min)

1. *"A dependency with a critical CVE lands in `site-service`."* Register the artifact by
   replaying `tests/fixtures/ci-payloads/site-service.json` with a Trivy SARIF report
   that contains one `CRITICAL` result (for example, from the target's `demo/vulnerable`
   branch).
2. The deployment request returns **Deny**: *"Rule MaxFindings failed: 1 CRITICAL
   finding … exceeds limit 0."* Open the finding to show the CVE and the affected package.
3. Show that no UI action overrides a Deny. The only way forward is a new artifact; a
   Security user may suppress a reviewed false positive by fingerprint, which applies to
   later scans and is audited.

## Journey C — Governed AI usage (~4 min, needs `--profile ai`)

The chat playground UI (P6-09) is not built yet. Send the prompt through the API with the
developer's access token (`POST /api/v1/ai/chat`, see [08 AI gateway](08-ai-gateway.md)).

1. As the developer, send a synthetic stack trace containing a fake AWS key such as
   `AKIA` followed by 16 placeholder characters (never a real key).
2. With the team's policy set to **Redact**, the reply arrives and the stored preview
   shows `[REDACTED:AWS_ACCESS_KEY]`. Point out the rate-limit headers.
3. Switch the team policy to **Block** and send it again: **422**, and the model is never
   called. This also works if Ollama is down.
4. Right window → **AI Governance** → AI request history: filter by detected type `AWS_ACCESS_KEY` to show
   both requests, their outcomes and redaction counts, with no prompt content stored.

## Journey D — Security engineer tightens policy (~3 min)

1. Right window → **Policies** → `Production baseline`: add a `DeploymentWindow` rule
   that blocks Fri 18:00 → Mon 08:00 in `Asia/Kuala_Lumpur`.
2. Save. The policy version increments and **Audit log** shows the policy change.
3. Simulate a Friday 20:00 MYT request with `POST /api/v1/policies/simulate` (there is no
   UI button yet). The result is **Deny**, with the window explanation. A real request inside the window is denied the same way.

## Reset

`docker compose down` keeps data. `reset-demo.sh` (Phase 8) is not written yet. Until
it exists, reset deliberately by removing the stack's volumes, which deletes the
database and reports, and re-seed. Never do this on a stack whose data you need.
