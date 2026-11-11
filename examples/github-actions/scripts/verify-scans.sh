#!/usr/bin/env bash
# verify-scans.sh — fail CI unless gitleaks.sarif, semgrep.sarif and trivy.sarif are real scanner output.
# Part of the AegisOps "connect your repository" example (docs/connect-your-repository.md).
#
# Each file must be SARIF 2.1.0 whose tool name matches its scanner and that has a results array.
# A missing or hand-written "clean" report must never reach AegisOps: the Staging gate policy trusts these scans.
# Usage: verify-scans.sh gitleaks.sarif semgrep.sarif trivy.sarif   (needs jq)
set -euo pipefail
if [ "$#" -ne 3 ]; then echo 'Usage: verify-scans.sh gitleaks.sarif semgrep.sarif trivy.sarif' >&2; exit 2; fi
for scanner in gitleaks semgrep trivy; do
  file="${scanner}.sarif"
  found=false
  for arg in "$@"; do [ "$arg" != "$file" ] || found=true; done
  [ "$found" = true ] && [ -s "$file" ] || { echo "Missing $file" >&2; exit 1; }
  jq -e --arg scanner "$scanner" '
    .version == "2.1.0" and (.runs | type == "array" and length > 0) and
    all(.runs[]; (.tool.driver.name | strings | ascii_downcase | contains($scanner)) and
      (.results | type == "array"))' "$file" > /dev/null || {
    echo "Invalid or mismatched $file (no synthetic clean scan permitted)" >&2; exit 1;
  }
done
echo "Scanner output verified: gitleaks.sarif, semgrep.sarif, trivy.sarif"
