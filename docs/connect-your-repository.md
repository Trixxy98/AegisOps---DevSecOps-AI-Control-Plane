# Connect your repository — a step-by-step guide

This guide is for people who want AegisOps to guard **their own application**. At the end, every push to your
`main` branch builds a container image, scans it, reports the result to AegisOps, and asks AegisOps to deploy it
to **Staging**. AegisOps then applies its rules, asks for approvals if needed, and runs the container.

You do not need to know AegisOps internals. If words like *artifact*, *SARIF* or *policy* are new, read the
glossary in the [user manual](user-manual.md#2-words-you-will-see-glossary) first; it takes five minutes.

> **Ready-made files.** Everything in this guide also ships as files you can copy:
> [`examples/github-actions/aegisops-ci.yml`](../examples/github-actions/aegisops-ci.yml) and the scripts in
> [`examples/github-actions/scripts/`](../examples/github-actions/scripts/). The report action is documented in
> [`.github/actions/aegisops-report/README.md`](../.github/actions/aegisops-report/README.md).

**The big picture**

```text
your repository (GitHub)                                   your AegisOps machine
┌──────────────────────────────────────────┐              ┌───────────────────────────────────────┐
│ build + test ─► image ─► scans (SARIF)   │              │ self-hosted runner ─► AegisOps API    │
│   (GitHub-hosted runners, ubuntu-latest) │──artifacts──►│   register job        (127.0.0.1:8080)│
└──────────────────────────────────────────┘              │                         │ policy        │
                         push image ─► ghcr.io ◄── pull ──│ Worker ◄────────────────┘ approvals     │
                                                          │   └─► Docker container (your app)     │
                                                          └───────────────────────────────────────┘
```

---

## 1. Before you start: what your application needs

| Need | Why | How to check |
| --- | --- | --- |
| A **Dockerfile** that builds a working image | AegisOps deploys container images, not source code | `docker build -t myapp .` and `docker run -p 8080:8080 myapp` work on your laptop |
| A **health endpoint** that answers HTTP **200** when the app is ready, for example `GET /health` | After starting your container, AegisOps calls it; if it does not answer 200 in time, the deployment fails and the old version is restored | `curl -i http://127.0.0.1:8080/health` shows `200 OK` |
| The app **listens on one port inside the container** (for example 8080) | AegisOps maps one host port to one container port | `EXPOSE 8080` in the Dockerfile, and the app binds to `0.0.0.0`, not `localhost` |
| An **image registry** the AegisOps Worker can pull from | The Worker downloads the exact image that was scanned | This guide uses GitHub Container Registry (`ghcr.io`), see below |
| **Tests** that can write JUnit XML | Staging requires a real test summary (total, passed, failed, skipped) | Most test runners have a JUnit option (section 5.2) |

**About the registry.** The workflow pushes `ghcr.io/<your-account>/<image-name>:<version>` with the built-in
`GITHUB_TOKEN`; you do not create any registry password for pushing. For **pulling**, the simplest option is to
make the package **public** (GitHub → your profile or organization → *Packages* → the package → *Package
settings* → *Change visibility*). Private packages need credentials on the AegisOps machine (section 9,
"Image pull failed").

**About the network: why a self-hosted runner?** AegisOps v1 only listens on `127.0.0.1` (your own machine). The
GitHub-hosted runners in the cloud cannot reach it. So the last job of the workflow, the one that talks to
AegisOps, must run on a **self-hosted runner installed on the AegisOps machine**. Building and scanning still run
on GitHub's free hosted runners. The pinned actions use Node 24, so a self-hosted runner needs version 2.327.1 or
later (runners update themselves by default).

> **Security note for public repositories.** A self-hosted runner executes workflow code on your computer.
> The example never runs the AegisOps job for pull requests, and you should also keep GitHub's default
> protection: *Settings → Actions → General → "Fork pull request workflows from outside collaborators"* →
> **"Require approval for all outside collaborators"**. Never give the runner more rights than it needs.

---

## 2. In AegisOps: create the project

You need an AegisOps account that is **Admin**, or **Owner** of a team (an Admin makes you Owner under
*Administration → Teams*). On a demo installation, `developer@aegisops.local` is Owner of the team
*network-platform*.

1. Sign in and open **Projects**.
2. Under the project list, fill in **Create project**:
   - **Team** — only teams you own are listed.
   - **Name** — a human name, for example `Orders service`.
   - **Slug** — the short ID CI uses in API calls, for example `orders-service`. Use **lowercase letters, digits
     and hyphens**. It is filled in from the name, and it **cannot be changed later**.
   - **Description** — optional.
3. Click **Create project**. The new project opens. It already has three environments: **Development**,
   **Staging** and **Production**, all using the **noop** target (a simulation that starts nothing).

If you see an error under the form, it is the API's answer; for example *"A project with this slug already
exists."* means you need another slug.

## 3. In AegisOps: configure the Staging environment

Still in **Projects → your project → Environments**, find **Staging** and fill in its target:

| Field | Value | Rules |
| --- | --- | --- |
| Target type | `docker` | `noop` only simulates; `docker` really runs your container |
| Container name | for example `orders-staging` | 3–63 characters: lowercase letters, digits, hyphens. Must be unique on the machine |
| Network | for example `aegisops-staging` (or empty) | Must be named `aegisops-<name>`. AegisOps creates it if needed. The control-plane network `aegisops` and Docker's `host`/`bridge` are refused |
| Host port | for example `8110` | **1024–65535**, and not used by anything else on the machine (AegisOps itself uses 3000, 8080, 5432, 6380; the demo services use 8101–8103 and 8201–8203) |
| Container port | the port your app listens on inside the container, for example `8080` | |
| Health path | for example `/health` | Starts with `/`, no spaces |
| Health timeout (seconds) | `60` | How long AegisOps waits for HTTP 200 |

Click **Save target**. If a rule is broken, the error explains which one.

**Optional — configuration and secrets.** If your app needs settings (a connection string, a token), add them in
the **Variables** box under Staging. Tick **Secret** for passwords and tokens: they are stored encrypted and never
shown again. They reach the container on the next deployment. Details:
[user manual, "Configuration and secrets"](user-manual.md#configuration-and-secrets-projects-screen).

## 4. In AegisOps: create a project API key

1. In **Projects → your project → API keys**, type a name such as `github-actions` and click **Create**.
2. **Copy the key now.** It starts with `aok_` and is shown **only once**. Keep it in your password manager until
   you paste it into GitHub in the next step; do not save it in a file in your repository.

The key belongs to this one project, expires after 90 days, and can only do three things:
`artifacts:write` (register a built version), `scans:write` (upload scan results) and `deployments:request`
(ask for a deployment). It cannot approve, change policies or read other projects. You can revoke it at any time
with **Revoke**.

## 5. In GitHub: connect the repository

### 5.1 Variables, secret and runner

1. **Repository variable** — *Settings → Secrets and variables → Actions → Variables → New repository
   variable*: name `AEGISOPS_URL`, value `http://127.0.0.1:8080` (the API address as seen from the runner on the
   AegisOps machine; no trailing slash).
2. **Repository secret** — same page, *Secrets → New repository secret*: name `AEGISOPS_API_KEY`, value the
   `aok_…` key from step 4.
3. **Self-hosted runner** — *Settings → Actions → Runners → New self-hosted runner*. Pick your operating system
   and run the commands GitHub shows **on the AegisOps machine**. When `config.sh` asks for additional labels,
   type `aegisops` (the example workflow targets `runs-on: [self-hosted, aegisops]`, so it never lands on another
   self-hosted runner by accident). Then start it with `./run.sh` (or install it as a service with
   `./svc.sh install && ./svc.sh start` on Linux/macOS).
4. Make sure the runner machine has **bash**, **curl** and **jq** (`jq --version`; macOS: `brew install jq`,
   Ubuntu: `sudo apt install jq`). The report action needs them.
5. Check the connection from the runner machine: `curl -fsS http://127.0.0.1:8080/health/ready` should answer.

### 5.2 Add the workflow

1. Copy [`examples/github-actions/aegisops-ci.yml`](../examples/github-actions/aegisops-ci.yml) to
   `.github/workflows/aegisops-ci.yml` in your repository.
2. Copy the three scripts from [`examples/github-actions/scripts/`](../examples/github-actions/scripts/) to
   `scripts/` in your repository and keep them executable (`chmod +x scripts/*.sh`):
   - `image-meta.sh` — computes a **unique version for every run** and the image name;
   - `test-summary.sh` — turns JUnit XML test results into the summary JSON AegisOps expects;
   - `verify-scans.sh` — refuses missing or fake scan reports.
3. Change every line marked `# CHANGE ME`:
   - `AEGISOPS_PROJECT` — your project slug (`orders-service`);
   - `IMAGE_NAME` — your image name (`orders-service`), the image becomes `ghcr.io/<owner>/orders-service`;
   - the **Run tests** step — your own test command that writes JUnit XML into `test-results/`;
   - the branches to deploy and, if needed, the Docker build `context`.
4. Commit and push to `main`. Watch *Actions* in GitHub, then **Deployments** in AegisOps.

The complete workflow (identical to the example file):

```yaml
name: aegisops-ci

on:
  push:
    branches: [main] # CHANGE ME: the branches that should be deployed to Staging
    tags: ["v*"]
  pull_request:
  workflow_dispatch:
    inputs:
      environment:
        description: AegisOps environment to request
        type: choice
        options: [Staging, Production]

permissions:
  contents: read

env:
  AEGISOPS_PROJECT: orders-service # CHANGE ME: your AegisOps project slug
  IMAGE_NAME: orders-service # CHANGE ME: your image name (ghcr.io/<owner>/<IMAGE_NAME>)

jobs:
  build:
    name: Build and test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7

      # CHANGE ME: install your toolchain (actions/setup-dotnet, actions/setup-node, actions/setup-python, ...),
      # then run your tests so that they write JUnit XML into test-results/. Examples:
      #   .NET:   dotnet test --logger "junit;LogFilePath=$PWD/test-results/{assembly}.xml"   (JunitXml.TestLogger)
      #   Node:   npx jest --ci --reporters=default --reporters=jest-junit   (JEST_JUNIT_OUTPUT_DIR=test-results)
      #   Python: pytest --junitxml=test-results/junit.xml
      #   Java:   mvn verify, then use target/surefire-reports/*.xml
      - name: Run tests
        run: |
          echo "Replace this step with your test command (see the comments above)." >&2
          exit 1

      - name: Test summary for AegisOps
        run: bash scripts/test-summary.sh test-summary.json test-results/*.xml

      - uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7
        with:
          name: test-summary
          path: test-summary.json

  image:
    name: Build and push image
    needs: build
    if: github.event_name != 'pull_request'
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    outputs:
      version: ${{ steps.meta.outputs.version }}
      image: ${{ steps.meta.outputs.image }}
      digest: ${{ steps.build.outputs.digest }}
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7

      - id: meta
        name: Unique version and image name
        run: bash scripts/image-meta.sh

      - uses: docker/login-action@8aa5831fb8d48d8f76594c7ddde61ae58019f550 # v4.8.0
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: docker/setup-buildx-action@f87e5991a6d7451dcb8d9637bfbc97413f497069 # v4.4.1

      - id: build
        uses: docker/build-push-action@c3c9e263c25d99ce0380d002d59b67737d91b0dc # v7.4.0
        with:
          context: . # CHANGE ME if your Dockerfile is not at the repository root (also set "file:")
          push: true
          tags: ${{ steps.meta.outputs.image }}
          build-args: |
            VERSION=${{ steps.meta.outputs.version }}
            COMMIT_SHA=${{ github.sha }}

  security:
    name: Security scans (SARIF)
    needs: image
    if: github.event_name != 'pull_request'
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: read
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7
        with:
          fetch-depth: 0 # Gitleaks scans the whole Git history

      # Findings are reported to AegisOps, which decides; only scanner errors fail CI (--exit-code 0).
      - name: Gitleaks
        run: |
          docker run --rm -v "$PWD:/src" -w /src zricethezav/gitleaks:v8.24.3 \
            git --redact --report-format sarif --report-path /src/gitleaks.sarif --exit-code 0 .

      - name: Semgrep
        run: |
          # Add rule packs for your language, for example --config p/csharp, p/javascript, p/python, p/java.
          docker run --rm -v "$PWD:/src" -w /src semgrep/semgrep:1.177.0 semgrep scan \
            --config p/default --metrics=off --sarif --output semgrep.sarif

      - name: Authenticate to GHCR for the image scan
        uses: docker/login-action@8aa5831fb8d48d8f76594c7ddde61ae58019f550 # v4.8.0
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Trivy (image)
        uses: aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25 # v0.36.0
        with:
          image-ref: ${{ needs.image.outputs.image }}
          format: sarif
          output: trivy.sarif
          exit-code: "0"
          version: v0.71.0

      - name: Verify scanner output
        run: bash scripts/verify-scans.sh gitleaks.sarif semgrep.sarif trivy.sarif

      - uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7
        with:
          name: scans
          path: |
            gitleaks.sarif
            semgrep.sarif
            trivy.sarif

  register:
    name: Report to AegisOps
    needs: [image, security]
    # Never on pull requests: this job runs on your own machine and uses the AegisOps API key.
    if: github.event_name != 'pull_request'
    # AegisOps v1 listens on 127.0.0.1 only, so this job must run on a self-hosted runner on the AegisOps machine.
    runs-on: [self-hosted, aegisops]
    steps:
      - uses: actions/download-artifact@37930b1c2abaa49bbe596cd826c3c89aef350131 # v7
        with:
          name: test-summary

      - uses: actions/download-artifact@37930b1c2abaa49bbe596cd826c3c89aef350131 # v7
        with:
          name: scans

      # Pinned to the commit of the AegisOps report action's v1 tag; Dependabot (github-actions) keeps it current.
      # You may write @v1 instead, but a commit SHA cannot change under you.
      - uses: Trixxy98/AegisOps---DevSecOps-AI-Control-Plane/.github/actions/aegisops-report@942fea38d724e7ce14f61f6de06113500a358e3b # v1
        with:
          url: ${{ vars.AEGISOPS_URL }}
          api-key: ${{ secrets.AEGISOPS_API_KEY }}
          project: ${{ env.AEGISOPS_PROJECT }}
          version: ${{ needs.image.outputs.version }}
          commit-sha: ${{ github.sha }}
          branch: ${{ github.ref_name }}
          image: ${{ needs.image.outputs.image }}
          digest: ${{ needs.image.outputs.digest }}
          test-summary: test-summary.json
          scans: gitleaks.sarif,semgrep.sarif,trivy.sarif
          deploy-to: ${{ github.event.inputs.environment || 'Staging' }}
```

**What the pieces mean**

- **Unique versions.** AegisOps registers each version **once**. `image-meta.sh` builds versions like
  `v0.0.0-main.1a2b3c4-run.123456789.1` (branch, short commit, run ID, attempt). A tag `v1.4.0` becomes
  `v1.4.0-run.<id>.<attempt>`. So a re-run never collides with an earlier build.
- **The digest.** The image is registered with its `sha256:` digest, and AegisOps deploys by digest: the bytes it
  runs are exactly the bytes that were scanned.
- **Scans never fail the build on findings** (`--exit-code 0`, `exit-code: "0"`). AegisOps decides what is
  acceptable through its policies; CI fails only if a scanner did not produce a real report.
- **`@v1`** is the stable major tag of the report action, published by the AegisOps maintainer. It moves to the
  latest compatible v1 release. For maximum safety, replace `@v1` with a full commit SHA and a `# v1` comment,
  like the other actions. The `actions/download-artifact` lines keep a version tag; pin them by SHA in your
  repository too (Dependabot for `github-actions` keeps all SHAs current).
- **Production from CI.** *Actions → aegisops-ci → Run workflow → Production* only works if an Admin has allowed
  API-key Production requests for that environment (`allowApiKeyProduction`). Normally a person requests
  Production from the AegisOps UI (**Deploy Production** on the artifact), which then needs two approvals.

## 6. Other CI systems (GitLab CI, Jenkins, Azure Pipelines, …): plain curl

The report action is a small shell script. Any CI system can make the same three calls with `curl` and `jq`, as
long as the job runs on a machine that can reach the AegisOps API (in v1: the AegisOps machine itself, for example
a GitLab shell runner or a Jenkins agent there). Keep the API key in your CI system's secret store and expose it as
the environment variable `AEGISOPS_API_KEY`; never print it.

Replace the `<…>` placeholders. `KEY` must be unique per pipeline run and the same when the same run retries
(it is used for the `Idempotency-Key` header; at most 128 characters).

```bash
set -euo pipefail
AEGISOPS_URL=http://127.0.0.1:8080
PROJECT=<project-slug>                       # e.g. orders-service
VERSION=<unique-version>                     # e.g. v0.0.0-main.1a2b3c4-run.987.1
IMAGE=<registry>/<owner>/<image>:<version>   # the pushed image
DIGEST=<sha256:…>                            # the pushed image digest (recommended)
KEY=<pipeline-id>-<attempt>-${VERSION}       # unique per run, stable across retries of the same run

# 1. Register the artifact (needs artifacts:write). 201 = created; 200 = same build already registered;
#    409 = this version exists for a different build.
artifact=$(curl -fsS -X POST "${AEGISOPS_URL}/api/v1/projects/${PROJECT}/artifacts" \
  -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
  -H "Idempotency-Key: artifact-${KEY}" \
  -H "Content-Type: application/json" \
  -d "$(jq -n --arg version "$VERSION" --arg sha "<full-commit-sha>" --arg branch "<branch>" \
        --arg image "$IMAGE" --arg digest "$DIGEST" --arg run "<pipeline-id>" \
        --argjson tests "$(cat test-summary.json)" \
        '{version:$version, commitSha:$sha, branch:$branch, imageReference:$image, imageDigest:$digest,
          ciProvider:"gitlab-ci", ciRunId:$run, buildStatus:"Passed", testStatus:"Passed", testSummary:$tests}')")
artifact_id=$(printf '%s' "$artifact" | jq -er '.id')

# 2. Upload each SARIF report (needs scans:write). Scanner is Gitleaks, Semgrep or Trivy; at most 20 MB each.
for scanner in Gitleaks Semgrep Trivy; do
  file="$(printf '%s' "$scanner" | tr '[:upper:]' '[:lower:]').sarif"
  curl -fsS -X POST "${AEGISOPS_URL}/api/v1/artifacts/${artifact_id}/scans" \
    -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
    -F "scanner=${scanner}" -F "format=Sarif" -F "report=@${file}"
done

# 3. Request a deployment (needs deployments:request). environmentTier: Development, Staging or Production.
curl -fsS -X POST "${AEGISOPS_URL}/api/v1/deployments" \
  -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
  -H "Idempotency-Key: deploy-${KEY}" \
  -H "Content-Type: application/json" \
  -d "$(jq -n --arg id "$artifact_id" '{artifactId:$id, environmentTier:"Staging", reason:"CI request"}')"
```

`test-summary.json` has the form `{"total":42,"passed":41,"failed":0,"skipped":1,"durationSeconds":12}`
(`examples/github-actions/scripts/test-summary.sh` creates it from JUnit XML). Before uploading, run
`verify-scans.sh` so a missing report fails the pipeline instead of reaching AegisOps as a "clean" result. To
practise these calls without any CI, see [user manual, Tutorial 1](user-manual.md#tutorial-1--register-your-first-artifact-and-see-your-first-deny)
(`scripts/ci-replay.sh`).

## 7. What happens next

1. **Policy decision.** AegisOps evaluates the deployment against the policies for Staging. With the default
   rules: tests must have passed, there must be **fresh Gitleaks, Semgrep and Trivy scans** (at most 7 days old),
   **no Critical finding**, and more than 10 High findings gives a warning.
   The result is **Allow**, **RequireApproval** or **Deny**.
2. **Approvals.** If a rule requires approval, people with the Approver or Security role see the request under
   **Approvals** (with a pop-up) and approve or reject it. Nobody can approve their own request. Production needs
   two approvals by default.
3. **Deployment.** The Worker checks the rules once more, pulls the image by digest, starts the container
   `orders-staging` on host port `8110`, waits for `GET /health` to return 200, and only then removes the previous
   version. If the health check fails, the new container is removed and the previous one restored.
4. **Where to watch it.** **Deployments → your deployment** shows the decision, each rule with pass/fail, and a
   live timeline (*Requested → Policy evaluated → … → Succeeded*). **Findings** lists what the scanners found. The
   **Audit log** (Admin, Security) records every step. Your app is then at `http://127.0.0.1:8110`.

## 8. Checklist

- [ ] The image builds and `GET <health path>` returns 200 locally.
- [ ] The GHCR package is public, or the Worker has registry credentials.
- [ ] Project created; Staging target is `docker` with a free host port ≥ 1024 and an `aegisops-<name>` network.
- [ ] API key created and stored as the `AEGISOPS_API_KEY` secret; `AEGISOPS_URL` variable set.
- [ ] Self-hosted runner with label `aegisops` is *Idle* on the AegisOps machine, with bash, curl and jq.
- [ ] Workflow and scripts copied; every `# CHANGE ME` changed.

## 9. Troubleshooting

| Problem | What it means and what to do |
| --- | --- |
| **HTTP 409 "An artifact with this version already exists."** | That version was already registered for a different build. Every run needs a unique version: keep `image-meta.sh` (it adds the run ID and attempt), and do not hard-code versions. Re-running the *same* run attempt is safe (AegisOps answers 200). |
| **The `Report to AegisOps` job stays "Queued" / "Waiting for a runner"** | No online runner has the labels `self-hosted` and `aegisops`. Check *Settings → Actions → Runners*: the runner must be *Idle*, have the `aegisops` label, and its `./run.sh` (or service) must be running on the AegisOps machine. For organization runners, check that the runner group allows this repository. |
| **The job fails with "Missing SARIF report", "Invalid SARIF" or "no synthetic clean scan permitted"** | A scanner did not produce a real report. Open the scanner step's log (Docker pull errors, Semgrep rule download errors, Trivy cannot pull a private image). AegisOps deliberately refuses missing or hand-made "clean" reports, and Staging requires all three: Gitleaks, Semgrep and Trivy, in that order. |
| **"Staging requires a real test summary"** | `test-summary.json` is missing, or reports 0 tests, or failures. Check that your test step writes JUnit XML into `test-results/`. |
| **Artifact registration failed (HTTP 401, 403 or 404 "Project was not found.")** | The API key is wrong, revoked or expired (90 days), or it belongs to another project, or `AEGISOPS_PROJECT` is not your slug. Check the slug; if needed create a new key and update the secret. |
| **`curl: (7) Failed to connect`** | The runner cannot reach `AEGISOPS_URL`. Is AegisOps running (`docker compose ps`)? Is the runner on the same machine? Is the variable exactly `http://127.0.0.1:8080`? |
| **The deployment is "Denied"** | Open it in **Deployments**: each failed rule is listed with its reason. Common ones: *scan required* (a scan is missing or older than 7 days), *max findings* (a Critical finding: fix it, or have Security suppress a reviewed false positive under **Findings**), *tests passed* (the test status was not Passed), or *outside allowed window*. A Deny is final for that request; fix the cause and push again. |
| **"API key deployments to production are not allowed for this target"** | Production from CI is off by default. Request Production from the AegisOps UI instead. |
| **Deployment "Failed" with "Image pull failed"** | The Worker could not download the image. If the GHCR package is **private**, either make it public, or give the Worker read access: it reads `DOCKER_GHCR_USER` and `DOCKER_GHCR_TOKEN` (a GitHub personal access token with only `read:packages`). The default `docker-compose.yml` does not pass them, so add them to the `worker` service's `environment` in a Compose override file and recreate the Worker. This private-registry path is not yet verified end to end (see `docs/v1-progress-audit.md`, P4-01). On Apple Silicon, an image built only for `linux/amd64` is pulled for that platform automatically and runs under emulation (slower). |
| **Deployment "Failed" with "Health check failed"** | The container started but `GET <health path>` did not return 200 within the timeout. Check the container port, that the app binds to `0.0.0.0`, the health path, and the app's configuration (missing variables or secrets). Raise the timeout for slow starts. The previous version was restored. |
| **"Host port must be between 1024 and 65535" / "Network must be … aegisops-<name>"** | Fix the Staging target fields (section 3). |

Still stuck? Read the deployment's timeline and the Worker log (`docker compose logs worker`). Found a bug? Open
an issue. Found a security problem? Follow [SECURITY.md](../SECURITY.md).
