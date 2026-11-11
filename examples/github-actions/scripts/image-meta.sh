#!/usr/bin/env bash
# image-meta.sh — compute a unique artifact version and GHCR image reference for this CI run.
# Part of the AegisOps "connect your repository" example (docs/connect-your-repository.md).
#
# AegisOps registers each version once (a second, different build with the same version gets HTTP 409),
# so the version always ends with the run ID and attempt: a re-run never reuses an existing version.
#   tag v1.4.0          -> v1.4.0-run.<run id>.<attempt>
#   branch main         -> v0.0.0-main.<short sha>-run.<run id>.<attempt>
# Inputs (environment): IMAGE_NAME (required, e.g. orders-service) and the standard GitHub Actions variables.
# Outputs (GITHUB_OUTPUT): version, image (ghcr.io/<owner>/<IMAGE_NAME>:<version>).
set -euo pipefail
: "${IMAGE_NAME:?Set IMAGE_NAME to your image name, for example orders-service}"
: "${GITHUB_RUN_ID:?}" "${GITHUB_RUN_ATTEMPT:?}" "${GITHUB_OUTPUT:?}" "${GITHUB_REPOSITORY_OWNER:?}" "${GITHUB_REF:?}"

if [[ "$GITHUB_REF" == refs/tags/v* ]]; then
  base="${GITHUB_REF#refs/tags/}"
else
  # Docker tags allow letters, digits, '_', '.' and '-' only.
  ref=$(printf '%s' "${GITHUB_REF_NAME:-unknown}" | tr -c 'A-Za-z0-9_.-' '-')
  base="v0.0.0-${ref}.$(git rev-parse --short HEAD)"
fi
version="${base}-run.${GITHUB_RUN_ID}.${GITHUB_RUN_ATTEMPT}"
version="${version:0:128}"
owner=$(printf '%s' "$GITHUB_REPOSITORY_OWNER" | tr '[:upper:]' '[:lower:]')
name=$(printf '%s' "$IMAGE_NAME" | tr '[:upper:]' '[:lower:]')
image="ghcr.io/${owner}/${name}:${version}"
echo "version=${version}" >> "$GITHUB_OUTPUT"
echo "image=${image}" >> "$GITHUB_OUTPUT"
echo "Version: ${version}"
echo "Image:   ${image}"
