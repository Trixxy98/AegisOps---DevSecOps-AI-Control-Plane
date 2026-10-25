#!/usr/bin/env bash
# Offline contract test for the composite action's HTTP payloads (no credentials or Docker).
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

cat > "$tmp/curl" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
url=''
body=''
scanner=''
key=''
while (($#)); do
  case "$1" in
    http*) url=$1; shift ;;
    -d) body=$2; shift 2 ;;
    -F) [[ $2 == scanner=* ]] && scanner=${2#scanner=}; shift 2 ;;
    -H) [[ $2 == Idempotency-Key:* ]] && key=${2#Idempotency-Key: }; shift 2 ;;
    *) shift ;;
  esac
done
jq -nc --arg url "$url" --arg body "$body" --arg scanner "$scanner" --arg key "$key" \
  '{url:$url,body:$body,scanner:$scanner,key:$key}' >> "$REQUESTS"
case "$url" in
  */artifacts) echo '{"id":"11111111-1111-1111-1111-111111111111"}' ;;
  */deployments) echo '{"id":"22222222-2222-2222-2222-222222222222","status":"Queued"}' ;;
  *) echo '{"status":"Completed"}' ;;
esac
MOCK
chmod +x "$tmp/curl"
export PATH="$tmp:$PATH" REQUESTS="$tmp/requests"
export AEGISOPS_URL=http://localhost:8080 AEGISOPS_API_KEY=test-key AEGISOPS_PROJECT=circuit-service
export AEGISOPS_VERSION=v1.0.0 AEGISOPS_SHA=0123456789abcdef0123456789abcdef01234567
export AEGISOPS_BRANCH=main AEGISOPS_IMAGE=example.invalid/circuit:v1.0.0 AEGISOPS_DIGEST=''
export AEGISOPS_KEY=123-1-v1.0.0 AEGISOPS_DEPLOY=Staging AEGISOPS_SUMMARY='' GITHUB_RUN_ID=123
export AEGISOPS_SCANS=''

bash "$root/.github/actions/aegisops-report/report.sh" > /dev/null
jq -e -s 'length == 2 and
  .[0].key == "artifact-123-1-v1.0.0" and
  (.[0].body | fromjson | .version == "v1.0.0" and .imageDigest == null and .testSummary == null and .ciRunId == "123") and
  .[1].key == "deploy-123-1-v1.0.0" and
  (.[1].body | fromjson | .artifactId == "11111111-1111-1111-1111-111111111111" and .environmentTier == "Staging")' "$REQUESTS" > /dev/null

echo '{"total":1,"passed":1,"failed":0,"skipped":0,"durationSeconds":1}' > "$tmp/summary.json"
echo '{"version":"2.1.0","runs":[]}' > "$tmp/gitleaks.sarif"
export AEGISOPS_SUMMARY="$tmp/summary.json" AEGISOPS_SCANS="$tmp/gitleaks.sarif"
export AEGISOPS_DIGEST=sha256:abcdef
: > "$REQUESTS"
bash "$root/.github/actions/aegisops-report/report.sh" > /dev/null
jq -e -s 'length == 3 and
  (.[0].body | fromjson | .imageDigest == "sha256:abcdef" and .testSummary.passed == 1) and
  .[1].scanner == "Gitleaks" and
  (.[1].url | endswith("/artifacts/11111111-1111-1111-1111-111111111111/scans")) and
  (.[2].url | endswith("/deployments"))' "$REQUESTS" > /dev/null

export AEGISOPS_SCANS="$tmp/missing-trivy.sarif"
: > "$REQUESTS"
if bash "$root/.github/actions/aegisops-report/report.sh" > /dev/null 2>&1; then
  echo 'Missing scan should fail the action' >&2
  exit 1
fi
[ ! -s "$REQUESTS" ] || { echo 'Missing scan sent a request' >&2; exit 1; }
echo 'PASS: CI report payloads, scan upload order and missing-scan fail-closed behavior'
