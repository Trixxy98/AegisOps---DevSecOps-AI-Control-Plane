# Staging site-service upgrade and health-failure rollback (Development Compose only)

Do not run this against Production or another Docker target. The API's rollback endpoint
**requests a new deployment of a predecessor artifact**; the worker's automatic
`RolledBack` event on a failed health check instead restores the stopped container.
These are different operations. This exercise tests the latter.

## Current blocker / image preparation

The existing `site-service` Staging artifact is the same digest as the running
container. The other two registered artifacts have failed Semgrep scans. Do not
claim either as a successful upgrade, fabricate scans, reuse an artifact version
for a different build, or deploy an unscanned local image.

The sibling repo's `demo/vulnerable` working tree contains uncommitted fixture
changes but also deliberately insecure demo code, so **do not push or deploy that
branch**. A separate, uncommitted worktree based on `main` is prepared at
`/private/var/folders/xm/rvmrs4y967q0z4421dzcjwvc0000gn/T/opencode/aegisops-site-rollback`
on `demo/rollback-validation`. It has the Staging-only fixture and a CI gate;
`AegisOpsFixture__FailHealth=true` makes `/health` return 503 only in Staging.
No branch has been pushed and no artifact has been registered by this preparation.
The existing `main` workflow gates registration to main/tags; `demo/**` pushes
by themselves do **not** register or deploy. The opt-in gate in the prepared
worktree only permits registration on an exact `demo/rollback-validation` push
whose head commit message contains `[staging-rollback:healthy]` or
`[staging-rollback:failing]`; the image job checks that the committed Dockerfile
default matches the phase. Other demo branches do not acquire deploy access.
`workflow_dispatch` is intentionally disabled to avoid rebuilding an existing
version from an old commit. CI must publish **two separate, distinct-digest builds**:

1. On the dedicated main-based branch, commit the prepared files with
   `[staging-rollback:healthy]` in the head commit message; leave the Dockerfile
   default `false`. Push only that branch after authorization and preflight.
2. **Only after the first deployment is healthy**, change that branch's
   Dockerfile `ARG AEGISOPS_FIXTURE_FAIL_HEALTH=false` to `true`; commit with
   `[staging-rollback:failing]` and push only that branch. Each push receives a
   unique CI run/attempt image version, digest, tests, real Gitleaks/Semgrep/Trivy
   reports, registration and Staging deployment request.

This requires explicit operator authorization for **both** commits/pushes to
the external repository and the real Staging deployments. Confirm the effective
Staging branch policy allows `demo/rollback-validation` (or obtain a narrowly
scoped authorized policy change before either push); do not weaken Production
or broadly allow demo branches. Verify the image build digest differs and the
actual scans are present, not synthetic reports. Wait for the report job and
policy evaluation; if either denies the artifact, stop rather than bypassing it.

## Preflight (before either request)

- Check the project/environment target through the authorized API: project
  `site-service`, tier `Staging`, Docker name `site-service-staging`, port `8101`,
  network `aegisops-staging`. Verify `Production` is unchanged and is not the target.
- Record `docker inspect site-service-staging` ID, image digest, artifact and
  deployment labels, network and port bindings; check there is **no**
  `site-service-staging-previous` container. Check `http://localhost:8101/health`
  returns 200 and save `/version` (including commit SHA). Record the current
  Staging `currentArtifactId` and Production's `currentArtifactId` via the API.
- Once CI has registered the artifact, confirm it is associated with `site-service`,
  has a **different** digest, Passed build/tests, and completed recent Gitleaks,
  Trivy, and Semgrep reports. Confirm the policy evaluation is `Allow`; an
  approval or denial is not permission to skip a rule. Check the host port is bound only by the owned Staging
  container; never remove or rename a foreign container to clear it.

## Observe each CI-driven deployment

1. For the healthy build, wait for `ImagePulled`, `ContainerStarted`,
  `HealthCheckPassed`, and `Succeeded` timeline entries. Verify the new container
   **ID and digest** differ from preflight; `/health` is 200 and `/version` matches
   the new artifact commit in Staging. Record this healthy container ID and image.
2. For the second (failure) build, expect `ImagePulled`, `ContainerStarted`,
   `HealthCheckFailed`, `RolledBack`, and `Failed` in order after the 60-second
   health timeout. Verify Staging `currentArtifactId` still points to the healthy
   upgrade, `site-service-staging` has the **same container ID and image digest**
   as after step 1, the failed new container is gone, and `/health` is 200 with
   `/version` reporting the step-1 commit. A `RolledBack` event by itself does
   **not** prove that the restored service is healthy.
3. Confirm `site-service-staging-previous` is absent, Production's artifact and
   target have not changed, and no unrelated container ID changed. Preserve
   deployment IDs, evaluations and timelines for the test record.

Stop if any ownership, digest, scan, policy, or health assertion fails. If the
backup remains or the restored container is unhealthy, leave the state intact
for manual investigation; do not delete a container, database, or volume to make
the test pass. The executor does **not** health-check the restored predecessor
before recording `RolledBack`, so the post-restore `/health` check is mandatory.
