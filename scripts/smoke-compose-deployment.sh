#!/usr/bin/env bash
# Integration smoke test against the running Compose stack (Development only).
set -euo pipefail

: "${AEGISOPS_ADMIN_PASSWORD:?Set AEGISOPS_ADMIN_PASSWORD to the password printed by docker compose logs api}"
for tool in curl jq; do
  command -v "$tool" >/dev/null || { echo "Missing $tool" >&2; exit 1; }
done

base=${AEGISOPS_URL:-http://localhost:3000}
curl -fsS "$base/" | grep -q 'id="root"' || { echo "Web is not serving the app" >&2; exit 1; }
token=$(curl -fsS "$base/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg password "$AEGISOPS_ADMIN_PASSWORD" '{email:"admin@aegisops.local",password:$password}')" | jq -er '.accessToken')
auth=(-H "Authorization: Bearer $token")
team=$(curl -fsS "${auth[@]}" "$base/api/v1/teams" | jq -er '.[] | select(.slug == "network-platform") | .id')
slug="smoke-$(date +%s)-$$"
version="1.0.$(date +%s)-smoke.$$"
project=$(curl -fsS "${auth[@]}" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg id "$team" --arg slug "$slug" '{teamId:$id,name:$slug,slug:$slug}')" \
  "$base/api/v1/projects")
env_id=$(jq -er '.environments[] | select(.tier == "Development" and .targetType == "noop") | .id' <<<"$project")
artifact=$(curl -fsS "${auth[@]}" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg version "$version" '{version:$version,commitSha:"0123456789abcdef0123456789abcdef01234567",branch:"main",imageReference:"example.invalid/smoke:1",buildStatus:"Passed",testStatus:"Passed"}')" \
  "$base/api/v1/projects/$slug/artifacts")
artifact_id=$(jq -er '.id' <<<"$artifact")
deployment=$(curl -fsS "${auth[@]}" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg artifact "$artifact_id" --arg env "$env_id" '{artifactId:$artifact,environmentId:$env,reason:"compose smoke"}')" \
  "$base/api/v1/deployments")
id=$(jq -er '.id' <<<"$deployment")

for ((attempt = 0; attempt < 30; attempt++)); do
  status=$(curl -fsS "${auth[@]}" "$base/api/v1/deployments/$id" | jq -er '.status')
  if [[ "$status" == Succeeded ]]; then
    decision=$(curl -fsS "${auth[@]}" "$base/api/v1/deployments/$id/evaluation" | jq -er '.decision')
    curl -fsS "${auth[@]}" "$base/api/v1/deployments/$id/events" | jq -e 'any(.[]; .type == "Succeeded")' >/dev/null
    [[ "$decision" == Allow ]] || { echo "Unexpected policy decision: $decision" >&2; exit 1; }
    curl -fsS "${auth[@]}" "$base/api/v1/projects/$slug" |
      jq -e --arg environment "$env_id" --arg artifact "$artifact_id" \
        'any(.environments[]; .id == $environment and .currentArtifactId == $artifact)' >/dev/null || {
          echo "Deployment succeeded but Development does not point to artifact $artifact_id" >&2
          exit 1
        }
    echo "PASS: $id ($slug) evaluated Allow and deployed to Development via the worker"
    exit 0
  fi
  if [[ "$status" == Failed || "$status" == Denied ]]; then
    echo "Deployment $id ended $status" >&2
    exit 1
  fi
  sleep 2
done
echo "Timed out waiting for $id (last status: $status); inspect docker compose logs worker" >&2
exit 1
