# Staging site-service upgrade and health-failure rollback (Development Compose only)

Do not run this against Production or another Docker target. The API's rollback endpoint
**requests a new deployment of a predecessor artifact**; the worker's automatic
`RolledBack` event on a failed health check instead restores the stopped container.
These are different operations. This exercise tests the latter.

## Executed validation (October 27, 2026)

The CI-gated `demo/rollback-validation` branch published and registered two
distinct images. Both CI runs completed successfully, including tests, image
build, Gitleaks, Semgrep, Trivy, and AegisOps registration. The deliberately
failing image was not deployed until the healthy image had passed its Staging
health check.

| Phase | Commit / CI run | Artifact version | Digest | Deployment |
| --- | --- | --- | --- | --- |
| Healthy | `248ef1b`, run `40439154412` | `v0.0.0-demo-rollback-validation.248ef1b-run.40439154412.1` | `sha256:559c720805d749a5d7116c5fc5a4879cd31ab0a4fd577123186eae1d0318072b` | `01a17b4b-6f9a-74a4-ac58-2899345c5c54` — Succeeded |
| Forced health failure | `c8ab095`, run `40442951347` | `v0.0.0-demo-rollback-validation.c8ab095-run.40442951347.1` | `sha256:fc733b015389893895b27549d14d0365aac8f84c1f98eacf5a9c2144590dd07c` | `01a17b5b-3049-73e5-b9ce-782c31d1a1ab` — Failed after rollback |

Both deployment evaluations were `Allow`; the failing deployment was allowed
through policy because its build, tests, scans, and finding thresholds passed.
Its Docker health check—not a policy bypass—triggered the failure. The recorded
timeline was `ImagePulled` → `ContainerStarted` → `HealthCheckFailed` (no 200
within 60 seconds) → `RolledBack` → `Failed`. The healthy deployment recorded
`HealthCheckPassed` and `Succeeded` before the failing deployment was requested.

Post-run verification on November 5, 2026: Staging `currentArtifactId` still
points to the healthy artifact; `/health` returns `Healthy`; `/version` reports
the healthy commit `248ef1b`; and the running `site-service-staging` container
has the healthy deployment label and digest above. The failed deployment has no
remaining container, `site-service-staging-previous` is absent, and the staging
network contains only the expected service container. Production remains on
artifact `01a11baa-de54-765c-ae3e-9651bb629006` with a `noop` target, and the
deployment history contains no Production deployment during this exercise.

The deployment event schema does not retain Docker container IDs, so the exact
pre-failure and restored container-ID equality cannot be reconstructed from the
historical event log. The `RolledBack` event, unchanged healthy artifact, and
live post-run health/version checks verify restoration at the artifact/service
level. The previously documented `demo/vulnerable` worktree still contains
uncommitted changes and deliberately insecure demo code; do not push or deploy it.

## Preflight checklist (for a repeat run)

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

## Observe each CI-driven deployment (expected sequence)

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
