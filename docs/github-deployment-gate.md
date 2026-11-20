# GitHub deployment gate — AegisOps as a custom deployment protection rule

Roadmap v1.2 item 1. GitHub keeps the button, AegisOps supplies the decision: when a GitHub Actions job targets a
GitHub **environment** that has the AegisOps App enabled as a *custom deployment protection rule*, GitHub pauses the
job and asks AegisOps. AegisOps evaluates its policies against the artifact registered for that commit (tests,
scans, findings, approvals, deployment windows) and approves or rejects the waiting job, with the rule-by-rule
explanation as the review comment. GitHub then runs (or skips) the deployment job itself.

Custom deployment protection rules are free for **public** repositories; private repositories need GitHub
Enterprise. Everything below works without any paid service.

## 1. How it works

```text
GitHub Actions job (environment: Production)
        │ waits
        ▼
GitHub ── POST deployment_protection_rule (signed) ──► https://<your host>/api/v1/integrations/github/webhook
                                                          │ API: verify X-Hub-Signature-256, store gate row,
                                                          │      enqueue GitHubGateEvaluate (202 at once)
                                                          ▼
                                     Worker: repository → project, environment → environment, sha → artifact
                                        │ no match: reject on GitHub with the reason
                                        ▼
                                     normal AegisOps deployment (requested by "github-app")
                                     policy evaluation → approvals (UI) → re-evaluation before release
                                        │ Allow / approved                │ Deny / Rejected / Cancelled / Failed
                                        ▼                                 ▼
                                     GitHubGateCallback job: POST /repos/{owner}/{repo}/actions/runs/{run}/
                                     deployment_protection_rule { environment_name, state, comment }
                                        │ approved → deployment Succeeded "Released to GitHub"
                                        ▼
GitHub resumes (or fails) the waiting job
```

Details:

- **Mapping.** `repository.full_name` must equal the AegisOps project's repository (Project → repository,
  provider GitHub, case-insensitive). The GitHub environment name must equal an AegisOps environment name of that
  project (case-insensitive, e.g. GitHub `production` ↔ AegisOps `Production`). The commit (`deployment.sha`,
  falling back to `sha`) must be a registered artifact of the project (the newest wins if several). The AegisOps
  environment's target type must be **`github`** ("GitHub runs the deployment; AegisOps decides"). Any mismatch is
  rejected on GitHub at once with an explanation, for example *"AegisOps: no artifact registered for commit 9f2c0a1
  in project circuit-service. Register it from CI before the deploy job."*
- **Requester.** The deployment has no user or API-key requester; the gate row (`deploy.github_deployment_gates`)
  marks its source, the audit actor is `System` / `github-app`, and any human approver satisfies
  approver ≠ requester. The deployment's reason holds the workflow run URL.
- **Lifecycle.** Unchanged: policy evaluation, approvals, the re-evaluation before release, timeline, SignalR and
  audit. Where the Worker would run Docker, a `github` target instead queues the "approved" callback; the
  deployment stays *Deploying* until GitHub acknowledges it, then becomes *Succeeded* with the timeline event
  "Released to GitHub". Denied, Rejected, Cancelled and Failed deployments send "rejected" with the failed rules
  (comments are cut to GitHub's 1024-character limit).
- **Reliability.** The webhook answers 202 within one database transaction; mapping and callbacks are durable jobs
  (leases, retries with backoff, dead-letter, admin retry). A redelivered webhook (same `X-GitHub-Delivery`) is a
  no-op. The callback records GitHub's answer on the gate row before the deployment is finished, so a retried job
  never posts a second review. 5xx, 429, timeouts and an expired token are retried; any other refusal (for example
  422 because the run was cancelled or GitHub's own wait timed out) marks the deployment *Failed* with the HTTP
  status and is not retried. AegisOps has no approval expiry of its own: an un-actioned request waits until someone
  approves, rejects or cancels it in AegisOps, or until GitHub gives up on the run.
- **Manual requests.** Requesting or rolling back a deployment to a `github` environment from the UI or API is
  refused (409): re-run the GitHub workflow instead.
- **Tokens.** The Worker signs a GitHub App JWT (RS256, `iat` = now − 60 s, `exp` = now + 9 min, `iss` = App ID),
  exchanges it at `POST /app/installations/{id}/access_tokens` for an installation token limited to
  `deployments: write`, and caches that token until 5 minutes before it expires. Tokens, the JWT and the private key
  are never logged or stored. The review URL is built from `GitHubApp:ApiBaseUrl`, never from the webhook's callback
  URL, so a token cannot be sent elsewhere.

## 2. Create the GitHub App

On GitHub: **Settings → Developer settings → GitHub Apps → New GitHub App** (or the organisation's settings if the
repository belongs to an organisation).

| Field | Value |
| --- | --- |
| GitHub App name | Any unique name, e.g. `AegisOps gate (<your name>)` |
| Homepage URL | Your AegisOps URL or this repository's URL |
| Webhook → Active | ✓ |
| Webhook URL | `https://<tunnel-host>/api/v1/integrations/github/webhook` (section 4) |
| Webhook secret | A random string of **at least 32 characters**, e.g. `openssl rand -hex 32` (keep it for `.env`) |
| Repository permissions | **Actions: Read-only**, **Deployments: Read and write** (nothing else) |
| Subscribe to events | **Deployment protection rule** |
| Where can this App be installed? | Only on this account |

Then:

1. **Create GitHub App.** Note the **App ID** on the *General* page.
2. **Credentials → Key pairs → generate a private key** (left menu; older GitHub pages showed *Private keys* at the
   bottom of *General*). A `.pem` file downloads. Keep it out of every repository and delete it once it is in `.env`.
3. **Install App** (left menu) → your account → *Only select repositories* → pick the repository → Install.

## 3. Configure AegisOps

`.env` cannot hold the multi-line PEM, so base64 it onto one line (do this in your own terminal):

```bash
base64 < ~/Downloads/<app-name>.<date>.private-key.pem | tr -d '\n'
```

Add to `.env` (see `.env.example`):

```bash
GITHUB_APP_ENABLED=true
GITHUB_APP_ID=<App ID>
GITHUB_APP_WEBHOOK_SECRET=<the webhook secret from section 2>
GITHUB_APP_PRIVATE_KEY_BASE64=<the one-line base64 output>
# optional: link back to AegisOps in the GitHub comment
GITHUB_APP_PUBLIC_BASE_URL=https://<tunnel-host>
```

`docker-compose.yml` passes these to the api (signature check) and the worker (JWT and callbacks). Recreate both
services after changing them. With `GITHUB_APP_ENABLED=true`, a secret shorter than 32 characters, a key that is not
an RSA private key, or a missing App ID stops the api/worker at startup with a message naming the setting (never
the value). Configuration reference: [14 §1](14-devops-and-infrastructure.md#environment-variables-api--worker).

In AegisOps (Admin or the team's Owner):

1. **Repository of the project** must be `owner/repo` with provider GitHub. There is no UI field for it yet; set it
   once with the API as Admin or the team's Owner (the access token is the `accessToken` returned by
   `POST /api/v1/auth/login` with your email and password):

   ```bash
   curl -X PUT "http://127.0.0.1:8080/api/v1/projects/<slug>/repository" \
     -H "Authorization: Bearer <access token>" -H "Content-Type: application/json" \
     -d '{"provider":"GitHub","fullName":"<owner>/<repo>","defaultBranch":"main","htmlUrl":"https://github.com/<owner>/<repo>"}'
   ```
2. **Environments → the environment GitHub will target** (for example *Production*): **Target type → github
   (GitHub deploys, AegisOps decides) → Save target**. The GitHub environment must have the same name
   (case-insensitive).
3. Policies for that environment's tier apply as usual (Policies page).

## 4. Make AegisOps reachable from GitHub (Cloudflare Tunnel)

GitHub must reach the webhook over HTTPS. The web container (`127.0.0.1:3000`) already proxies `/api/` to the API.

**Quick, temporary URL** (no account needed; the URL changes every run):

```bash
cloudflared tunnel --url http://127.0.0.1:3000
# prints https://<random-words>.trycloudflare.com  → webhook URL is
# https://<random-words>.trycloudflare.com/api/v1/integrations/github/webhook
```

Update the App's webhook URL each time the quick tunnel restarts.

**Stable hostname on your own domain** (domain on Cloudflare, free plan):

```bash
cloudflared tunnel login
cloudflared tunnel create aegisops
cloudflared tunnel route dns aegisops aegisops.<your-domain>
cat > ~/.cloudflared/config.yml <<'EOF'
tunnel: aegisops
credentials-file: /Users/<you>/.cloudflared/<tunnel-id>.json
ingress:
  - hostname: aegisops.<your-domain>
    path: ^/api/v1/integrations/github/webhook$
    service: http://127.0.0.1:3000
  - service: http_status:404
EOF
cloudflared tunnel run aegisops
```

The `path` rule above exposes **only the webhook**; GitHub needs nothing else. Remove it (keep only the hostname)
if you also want to use the UI through the tunnel.

Any other HTTPS endpoint (the production TLS profile, a reverse proxy) works the same way.

## 5. Enable the rule on the repository

1. Repository → **Settings → Environments** → create or open the environment (e.g. `production`).
2. Under **Deployment protection rules**, tick the App's name → **Save protection rules**.
3. Give a job that environment, after the job that registers the artifact. Example for a repository already
   connected with [connect-your-repository.md](connect-your-repository.md):

```yaml
  register:
    # ... aegisops-report with deploy-to: ''   (register artifact and scans only; GitHub asks AegisOps later)

  deploy-production:
    needs: register
    runs-on: ubuntu-latest
    environment: production        # waits for AegisOps
    steps:
      - run: echo "Deploy ${{ github.sha }} here (GitHub runs the deployment)."
```

When the job starts, GitHub shows *Waiting* on the run page ("production waiting for AegisOps Gate"); the decision
appears under **Deployment protection rules** on the run's summary, e.g. *AegisOps Gate — passed: AegisOps approved
this deployment after 2 approval(s). Policies: Production baseline v4, Global baseline v1.*

The workflow file of the **branch you run** is used, so every branch you want to gate needs the new job. Pushing a
workflow change also starts a normal run: if that run registers with `deploy-to: Staging` and the self-hosted
runner is online, AegisOps deploys to Staging. Add `[skip ci]` to the commit message when you only change the
workflow.

## 6. Verified live

On 2026-11-20 with a GitHub App installed on `Trixxy98/aegisops-circuit-service`, a named Cloudflare Tunnel that
publishes only the webhook path (every other path answers 404 from the internet) and AegisOps on a laptop:
GitHub's signed `ping` was accepted (204) while unsigned and wrongly signed requests got 401; a manual Production
run was held by GitHub, AegisOps evaluated *Require approval (2)*, two approvers approved in AegisOps, the Worker
answered GitHub's review endpoint (204) and the `deploy-production` job ran. The run summary shows
*AegisOps Gate — passed* with the comment above. A second Production run from the `demo/vulnerable` branch
(an artifact with 11 open Critical findings) was denied without any human step: GitHub showed *AegisOps Gate —
failed* with "AegisOps denied this deployment by policy. 11 Critical finding(s) exceed limit 0" and the failed
rule `MaxFindings [Deny] (Global baseline v1)`, and the `deploy-production` job did not run.

## 7. Security notes

- The webhook is anonymous at the HTTP level: the **HMAC-SHA256 signature** (`X-Hub-Signature-256`) over the raw
  body is checked with a constant-time comparison before anything is parsed. Bodies over 1 MiB are refused (413).
  Bad signatures get 401, are logged and are audited at most once every 10 seconds (`github.webhook_rejected`), so a
  flood cannot grow the append-only audit table. While the integration is disabled the route answers 404.
- The webhook has its own rate-limit bucket per client address, separate from the anonymous API bucket, so a
  sign-in flood through the same tunnel address cannot starve GitHub's deliveries (GitHub does not redeliver
  automatically; failed deliveries can be redelivered from the App's *Advanced* page).
- Only routing data is stored (repository, environment, commit, run id, delivery id, installation id) and only
  repository/environment/short commit are logged. No payload body, signature or token is stored or logged.
- A tunnel that forwards the whole site exposes the AegisOps UI and API to the internet: keep strong passwords,
  rotate the demo accounts' passwords, and prefer the path-restricted ingress above.
- The private key can approve deployments in every repository the App is installed on: store it only in `.env`,
  install the App on as few repositories as possible, and rotate it (generate a new key, update `.env`, delete the
  old key on GitHub) if it leaks.

## 8. Troubleshooting

| Symptom | Check |
| --- | --- |
| GitHub *Recent Deliveries* shows 404 | `GITHUB_APP_ENABLED=true` in `.env` and the api recreated; webhook URL ends with `/api/v1/integrations/github/webhook` |
| 401 | The webhook secret in `.env` differs from the App's |
| 413 | A proxy in front changed the body; payloads are far below 1 MiB |
| 429 | Many deliveries from one address within seconds; redeliver from GitHub |
| Run rejected: "not connected to an AegisOps project" | Project repository is not `owner/repo` of this repository |
| Run rejected: "no environment named …" | Create the AegisOps environment with the GitHub environment's name |
| Run rejected: "uses the noop/docker target" | Set the environment's target type to `github` |
| Run rejected: "no artifact registered for commit …" | The register job must run before the protected job, for the same commit |
| AegisOps deployment stuck in *Deploying* | Administration → Jobs: a dead-lettered `GitHubGateCallback` (GitHub unreachable or `GitHubApp` not configured on the worker); fix and retry it |
| Deployment *Failed* "GitHub refused the decision (HTTP 422)" | The run was cancelled, re-run or timed out on GitHub before AegisOps answered |
| api/worker do not start | The log names the invalid `GitHubApp:*` setting |

What remains to verify live is listed in [roadmap-v1.2.md](roadmap-v1.2.md).
