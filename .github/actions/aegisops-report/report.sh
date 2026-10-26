#!/usr/bin/env bash
set -euo pipefail

summary=null
if [ -n "${AEGISOPS_SUMMARY}" ]; then
  [ -f "$AEGISOPS_SUMMARY" ] || { echo "Missing test summary: $AEGISOPS_SUMMARY" >&2; exit 1; }
  summary=$(jq -e 'select(type == "object" and (.total | type == "number") and .total > 0 and
    (.passed | type == "number") and .passed > 0 and (.failed == 0) and
    (.skipped | type == "number") and .total == .passed + .failed + .skipped)' "$AEGISOPS_SUMMARY")
fi

if [ "${AEGISOPS_DEPLOY}" = Staging ] && [ "$summary" = null ]; then
  echo 'Staging requires a real test summary' >&2; exit 1
fi

# Check inputs before registering: a missing report must not silently turn into a clean CI run.
files=()
scanners=()
if [ -n "${AEGISOPS_SCANS}" ]; then
  IFS=',' read -ra paths <<< "$AEGISOPS_SCANS"
  for file in "${paths[@]}"; do
    file="${file#"${file%%[![:space:]]*}"}"
    file="${file%"${file##*[![:space:]]}"}"
    [ -f "$file" ] || { echo "Missing SARIF report: $file" >&2; exit 1; }
    case "$file" in
      *gitleaks*) scanner=Gitleaks ;;
      *semgrep*) scanner=Semgrep ;;
      *trivy*) scanner=Trivy ;;
      *) echo "Unknown scanner for SARIF report: $file" >&2; exit 1 ;;
    esac
    jq -e --arg scanner "$scanner" '
      .version == "2.1.0" and (.runs | type == "array" and length > 0) and
      all(.runs[]; (.tool.driver.name | strings | ascii_downcase | contains($scanner | ascii_downcase)) and
        (.results | type == "array"))' "$file" > /dev/null || {
      echo "Invalid SARIF for $scanner: $file" >&2; exit 1;
    }
    files+=("$file")
    scanners+=("$scanner")
  done
fi

if [ "${AEGISOPS_DEPLOY}" = Staging ] &&
   { [ "${#scanners[@]}" -ne 3 ] ||
     [ "${scanners[0]}" != Gitleaks ] || [ "${scanners[1]}" != Semgrep ] || [ "${scanners[2]}" != Trivy ]; }; then
  echo 'Staging requires Gitleaks, Semgrep and Trivy SARIF in that order' >&2; exit 1
fi

body=$(jq -n \
  --arg version "$AEGISOPS_VERSION" \
  --arg sha "$AEGISOPS_SHA" \
  --arg branch "$AEGISOPS_BRANCH" \
  --arg image "$AEGISOPS_IMAGE" \
  --arg digest "$AEGISOPS_DIGEST" \
  --arg run "${GITHUB_RUN_ID:-}" \
  --argjson tests "$summary" \
  '{version:$version,commitSha:$sha,branch:$branch,imageReference:$image,imageDigest:(if $digest == "" then null else $digest end),ciProvider:"github-actions",ciRunId:$run,buildStatus:"Passed",testStatus:"Passed",testSummary:$tests}')
artifact_response=$(mktemp)
trap 'rm -f "$artifact_response"' EXIT
artifact_status=$(curl -sS -o "$artifact_response" -w '%{http_code}' -X POST "${AEGISOPS_URL}/api/v1/projects/${AEGISOPS_PROJECT}/artifacts" \
  -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
  -H "Idempotency-Key: artifact-${AEGISOPS_KEY}" \
  -H "Content-Type: application/json" \
  -d "$body")
if [[ $artifact_status != 2?? ]]; then
  # Only surface the API's problem title; never print the request or authorization headers.
  echo "Artifact registration failed (HTTP $artifact_status): $(jq -r '.title // "No problem title returned"' "$artifact_response" 2>/dev/null || echo 'No problem title returned')" >&2
  exit 1
fi
artifact=$(cat "$artifact_response")
artifact_id=$(printf '%s' "$artifact" | jq -er '.id | select(type == "string" and length > 0)')

for i in "${!files[@]}"; do
  curl -fsS -X POST "${AEGISOPS_URL}/api/v1/artifacts/${artifact_id}/scans" \
    -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
    -F "scanner=${scanners[i]}" \
    -F "format=Sarif" \
    -F "report=@${files[i]}"
done

if [ -n "${AEGISOPS_DEPLOY}" ]; then
  curl -fsS -X POST "${AEGISOPS_URL}/api/v1/deployments" \
    -H "Authorization: Bearer ${AEGISOPS_API_KEY}" \
    -H "Idempotency-Key: deploy-${AEGISOPS_KEY}" \
    -H "Content-Type: application/json" \
    -d "$(jq -n --arg id "$artifact_id" --arg tier "$AEGISOPS_DEPLOY" '{artifactId:$id,environmentTier:$tier,reason:"CI request"}')"
fi
