#!/usr/bin/env bash
# test-summary.sh — turn JUnit XML test results into the test summary JSON that AegisOps expects.
# Part of the AegisOps "connect your repository" example (docs/connect-your-repository.md).
#
# Most test runners can write JUnit XML (pytest --junitxml, jest-junit, Maven Surefire, Gradle,
# `dotnet test --logger junit` with JunitXml.TestLogger, go-junit-report, ...).
# Output: {"total":N,"passed":N,"failed":N,"skipped":N,"durationSeconds":N}
# AegisOps accepts it for Staging only when total > 0, passed > 0 and failed == 0, so this script fails
# when no tests ran or any test failed (errors count as failures).
# Usage: test-summary.sh <output.json> <junit.xml> [more.xml ...]   (needs python3)
set -euo pipefail
if [ "$#" -lt 2 ]; then echo 'Usage: test-summary.sh <output.json> <junit.xml> [more.xml ...]' >&2; exit 2; fi
output=$1
shift
python3 -I - "$output" "$@" <<'PY'
import json
import sys
import xml.etree.ElementTree as ElementTree

output, files = sys.argv[1], sys.argv[2:]
total = failed = skipped = 0
duration = 0.0
for path in files:
    root = ElementTree.parse(path).getroot()
    suites = [root] if root.tag == "testsuite" else root.iter("testsuite")
    for suite in suites:
        # Nested suites repeat their children's counts; count leaf suites only.
        if suite.find("testsuite") is not None:
            continue
        total += int(suite.get("tests", 0))
        failed += int(suite.get("failures", 0)) + int(suite.get("errors", 0))
        skipped += int(suite.get("skipped", suite.get("disabled", 0)))
        duration += float(suite.get("time", 0) or 0)
passed = total - failed - skipped
summary = {"total": total, "passed": passed, "failed": failed, "skipped": skipped, "durationSeconds": round(duration)}
with open(output, "w", encoding="utf-8") as handle:
    json.dump(summary, handle)
print(json.dumps(summary))
if total == 0 or passed <= 0:
    sys.exit("No passing tests found in the JUnit XML.")
if failed:
    sys.exit(f"{failed} test(s) failed.")
PY
