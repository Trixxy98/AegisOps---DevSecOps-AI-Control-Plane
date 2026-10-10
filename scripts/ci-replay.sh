#!/usr/bin/env bash
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "Usage: AEGISOPS_URL=http://localhost:8080 AEGISOPS_API_KEY=aok_... scripts/ci-replay.sh tests/fixtures/ci-payloads/site-service.json" >&2
  exit 1
fi

: "${AEGISOPS_URL:?Set AEGISOPS_URL}"
: "${AEGISOPS_API_KEY:?Set AEGISOPS_API_KEY}"

payload=$1
project=$(jq -r '.project' "$payload")
version=$(jq -r '.version' "$payload")
body=$(jq '{version,commitSha,branch,imageReference,imageDigest,buildStatus,testStatus,testSummary,ciProvider:"ci-replay"}' "$payload")
artifact=$(curl -fsS -X POST "${AEGISOPS_URL}/api/v1/projects/${project}/artifacts" \
  -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
  -H "Idempotency-Key: replay-artifact-${project}-${version}" \
  -H "Content-Type: application/json" \
  -d "$body")
echo "$artifact"
tier=$(jq -r '.deployTo // empty' "$payload")
if [ -n "$tier" ]; then
  artifact_id=$(printf '%s' "$artifact" | jq -r '.id')
  curl -fsS -X POST "${AEGISOPS_URL}/api/v1/deployments" \
    -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
    -H "Idempotency-Key: replay-deploy-${project}-${version}-${tier}" \
    -H "Content-Type: application/json" \
    -d "$(jq -n --arg id "$artifact_id" --arg environmentTier "$tier" '{artifactId:$id,environmentTier:$environmentTier,reason:"CI replay"}')"
  echo
fi
