# AegisOps report action

A composite GitHub Action that reports one CI build to [AegisOps](../../../README.md):

1. registers the built container image as an **artifact** of an AegisOps project (with its digest and test summary);
2. uploads the **Gitleaks, Semgrep and Trivy SARIF** reports for that artifact;
3. requests a **deployment** to an environment tier (Staging by default).

AegisOps then applies its policies and approvals and deploys the image. Step-by-step setup, including a complete
workflow: [docs/connect-your-repository.md](../../../docs/connect-your-repository.md). A ready-to-copy workflow:
[examples/github-actions/aegisops-ci.yml](../../../examples/github-actions/aegisops-ci.yml).

## Usage

```yaml
  register:
    needs: [image, security]
    if: github.event_name != 'pull_request'
    runs-on: [self-hosted, aegisops] # AegisOps v1 listens on 127.0.0.1: run on the AegisOps machine
    steps:
      # download the test-summary and scans artifacts first (actions/download-artifact)
      - uses: Trixxy98/AegisOps---DevSecOps-AI-Control-Plane/.github/actions/aegisops-report@v1
        with:
          url: ${{ vars.AEGISOPS_URL }}
          api-key: ${{ secrets.AEGISOPS_API_KEY }}
          project: orders-service
          version: ${{ needs.image.outputs.version }}
          commit-sha: ${{ github.sha }}
          branch: ${{ github.ref_name }}
          image: ${{ needs.image.outputs.image }}
          digest: ${{ needs.image.outputs.digest }}
          test-summary: test-summary.json
          scans: gitleaks.sarif,semgrep.sarif,trivy.sarif
          deploy-to: Staging
```

## Inputs

| Input | Required | Default | Description |
| --- | --- | --- | --- |
| `url` | yes | | AegisOps API base URL, without a trailing slash, as seen from the runner (for example `http://127.0.0.1:8080`). |
| `api-key` | yes | | Project API key (`aok_…`). Pass it from a repository secret, never inline. |
| `project` | yes | | Project slug in AegisOps. |
| `version` | yes | | Artifact version. Must be unique per build: AegisOps registers each version once (see "Exit behaviour"). |
| `commit-sha` | yes | | Git commit SHA. |
| `branch` | yes | | Git branch (or tag) name. |
| `image` | yes | | Image reference, for example `ghcr.io/<owner>/<image>:<version>`. |
| `digest` | no | `""` | Image digest (`sha256:…`). Strongly recommended: AegisOps then deploys exactly the scanned bytes. |
| `test-summary` | no | `""` | Path to a JSON file `{"total":N,"passed":N,"failed":0,"skipped":N,"durationSeconds":N}`. **Required when `deploy-to` is `Staging`**: total and passed must be > 0, failed must be 0, and total = passed + failed + skipped. |
| `scans` | no | `""` | Comma-separated SARIF paths. The scanner is detected from the file name (`gitleaks`, `semgrep`, `trivy`). For `Staging` all three are required, in the order Gitleaks, Semgrep, Trivy. Missing or invalid files fail the step. |
| `deploy-to` | no | `Staging` | Environment tier to request: `Development`, `Staging` or `Production`. |

## API key permissions

Create the key in AegisOps under **Projects → your project → API keys** (an Admin or the team's Owner can). A key
belongs to **one project** and the UI grants exactly the three scopes this action needs:

| Scope | Used for |
| --- | --- |
| `artifacts:write` | `POST /api/v1/projects/{slug}/artifacts` |
| `scans:write` | `POST /api/v1/artifacts/{id}/scans` |
| `deployments:request` | `POST /api/v1/deployments` |

A key cannot approve deployments, change policies or read other projects. Production requests with an API key
are refused unless an Admin enabled `allowApiKeyProduction` on that environment's target.

The runner needs `bash`, `curl` and `jq`.

## Outputs and exit behaviour

The action has no step outputs; its result is in AegisOps (**Deployments**). It prints the API's JSON responses
for the scan uploads and the deployment request, and never prints the API key or request headers.

The step **fails** (non-zero exit) when:

- the test summary is missing or invalid, or `deploy-to` is `Staging` without a valid test summary;
- a SARIF path is missing, is not SARIF 2.1.0 from the expected scanner, or cannot be mapped to a scanner;
- `deploy-to` is `Staging` without exactly Gitleaks, Semgrep and Trivy reports;
- artifact registration returns a non-2xx status. Only the Problem Details title is printed, for example
  `Artifact registration failed (HTTP 409): An artifact with this version already exists.`;
- a scan upload or the deployment request returns a non-2xx status (curl `--fail`).

Registering the same build again with the same version returns 200 and continues; a different build with an
existing version returns 409. Requests use `Idempotency-Key` headers derived from `github.run_id`,
`github.run_attempt` and `version`, so retries inside one run attempt are safe. A policy **Deny** is not a CI
failure: the request was accepted, and AegisOps records the decision and its reasons.

## Versioning

- `@v1` is a **floating major tag** maintained by the AegisOps maintainer. It points to the latest v1 release and
  only moves for backwards-compatible changes (same inputs, same behaviour). Breaking changes get `v2`.
- For maximum safety, **pin a full commit SHA** and keep the version as a comment, the same way third-party
  actions are pinned in AegisOps' own workflows:

  ```yaml
  - uses: Trixxy98/AegisOps---DevSecOps-AI-Control-Plane/.github/actions/aegisops-report@<40-character-commit-sha> # v1
  ```

  Dependabot (`package-ecosystem: github-actions`) can propose updates for SHA-pinned actions.
- `@dev` and `@main` follow development branches and may change at any time; do not use them in your workflows.
