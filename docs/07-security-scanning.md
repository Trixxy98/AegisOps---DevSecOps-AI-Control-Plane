# 07 — Security Scanning

AegisOps does not scan code itself (v1). It **ingests, normalizes, stores and counts**
scanner results so the policy engine can act on them.

## 1. Scanners

| Scanner | Finds | Runs against | Output consumed |
| --- | --- | --- | --- |
| **Gitleaks** | Hard-coded secrets: API keys, tokens, passwords, private keys | Git history / working tree of the target repo | SARIF (`--report-format sarif`) |
| **Trivy** | OS & library CVEs, misconfigurations (Dockerfile), exposed secrets in image layers | Built container image (`ghcr.io/…@sha256:…`) | SARIF (`--format sarif`) — optionally native JSON for richer fields |
| **Semgrep** | Insecure code patterns (SQLi, path traversal, weak crypto, hard-coded credentials) using `p/default`, `p/security-audit`, `p/csharp` rule packs | Source tree | SARIF (`--sarif`) |

```
                     Target repository (GitHub Actions)
                                  │
             ┌────────────────────┼────────────────────┐
             ▼                    ▼                    ▼
         Gitleaks               Semgrep               Trivy
        (source)               (source)              (image)
             │                    │                    │
             └────── SARIF ───────┼──────── SARIF ─────┘
                                  ▼
               POST /api/v1/artifacts/{id}/scans  (×3, API key)
                                  ▼
                 SarifParser → SecurityScan + SecurityFinding[]
                                  ▼
                          Summary counts (JSONB)
                                  ▼
                            Policy Engine
```

## 2. Two execution modes

| Mode | `ScanSource` | Phase | How |
| --- | --- | --- | --- |
| **CI-run (primary)** | `CiUploaded` | 3 | Actions run scanners, upload SARIF to AegisOps. Fast, no Docker socket needed, scanners always current |
| **Worker-run (optional)** | `WorkerExecuted` | 4 | Worker runs the scanner containers (`aquasec/trivy`, `zricethezav/gitleaks`, `semgrep/semgrep`) against the image / a shallow clone. Useful for re-scanning old artifacts against new CVE databases |

The policy engine does not care which mode produced a scan.

## 3. Ingestion API

`POST /api/v1/artifacts/{artifactId}/scans`

- Auth: API key with `scans:write` for that project, or user with `Security`/`Admin`.
- Body: `multipart/form-data` with fields `scanner` (`Gitleaks|Trivy|Semgrep`),
  `format` (`Sarif|TrivyJson|GitleaksJson|SemgrepJson`), `toolVersion` (optional),
  `report` (file, ≤ 20 MB).
- Behaviour: replaces any previous scan for `(artifact, scanner)` in one transaction;
  small reports (≤ 2 MB) are parsed inline, larger ones are stored and parsed via
  `ParseScanReport` job (scan `Status = Queued` until done).
- Response `201` with scan id, summary counts and `findingsUrl`.

Raw reports are stored on disk (`Storage:ReportsPath`, Docker volume) with path
`reports/{artifactId}/{scanner}-{timestamp}.sarif` for download/audit.

## 4. Normalization (SARIF → `SecurityFinding`)

SARIF 2.1.0 is the common denominator. One parser (`SarifReportParser`) plus small
per-tool "enrichers" for tool-specific properties.

| `SecurityFinding` field | SARIF source |
| --- | --- |
| `RuleId` | `result.ruleId` |
| `Title` | `rule.shortDescription.text` ?? `result.message.text` (first line) |
| `Description` | `result.message.text` |
| `FilePath`, `StartLine` | `result.locations[0].physicalLocation.artifactLocation.uri`, `region.startLine` |
| `HelpUri` | `rule.helpUri` |
| `Fingerprint` | `result.partialFingerprints`/`fingerprints` if present, else `sha256(scanner|ruleId|filePath|packageName|installedVersion)` |
| `Severity` | see mapping below |
| `PackageName`, `InstalledVersion`, `FixedVersion`, `Cve` | Trivy: parsed from `result.message`/`rule.properties`/`rule.help.text`; Semgrep/Gitleaks: null |

### Severity mapping

| Scanner | Primary source | Mapping |
| --- | --- | --- |
| Trivy | `rule.properties["security-severity"]` (CVSS score string) | ≥ 9.0 Critical · ≥ 7.0 High · ≥ 4.0 Medium · > 0 Low · else Unknown. Fallback: rule `properties.tags` contains `CRITICAL/HIGH/…` |
| Semgrep | `result.level` + `rule.properties["security-severity"]` if present | `error`→High, `warning`→Medium, `note`→Low; upgraded to Critical if score ≥ 9.0 |
| Gitleaks | every result is a secret | **Critical** by default (configurable `Scanning:Gitleaks:DefaultSeverity`) |

`Unknown` is counted separately and excluded by `MaxFindings` unless `includeUnknown = true`.

### Summary computation

After parsing, `SecurityScan.Summary` is recomputed from **open** findings (suppressed
excluded). Summaries are denormalized so the policy engine never scans findings tables.

## 5. Findings lifecycle

| Action | Who | Effect |
| --- | --- | --- |
| Suppress | `Security`, `Admin` | `Status = Suppressed`, reason required, audited; applies to **fingerprint** across artifacts of the same project (so a known false positive stays suppressed on the next build) |
| Unsuppress | same | `Status = Open` |
| Resolve | system | When a newer artifact of the project has no finding with the same fingerprint, previous findings are marked `Resolved` (informational) |

Suppressions are stored as `security.finding_suppressions (project_id, fingerprint, reason,
created_by, expires_at?)` and applied at ingestion time.

## 6. GitHub Actions snippet (target repositories)

```yaml
security:
  runs-on: ubuntu-latest
  needs: build
  steps:
    - uses: actions/checkout@v4
      with: { fetch-depth: 0 }                       # Gitleaks needs history

    - name: Gitleaks
      uses: gitleaks/gitleaks-action@v2
      env: { GITLEAKS_ENABLE_SUMMARY: "false" }
      with: { args: detect --report-format sarif --report-path gitleaks.sarif --exit-code 0 }

    - name: Semgrep
      run: |
        docker run --rm -v "$PWD:/src" semgrep/semgrep semgrep scan \
          --config p/default --config p/security-audit --sarif -o /src/semgrep.sarif || true

    - name: Trivy (image)
      uses: aquasecurity/trivy-action@0.28.0
      with:
        image-ref: ${{ needs.build.outputs.image }}
        format: sarif
        output: trivy.sarif
        exit-code: "0"                                # AegisOps decides, not CI

    - name: Upload scans to AegisOps
      run: |
        for s in Gitleaks:gitleaks.sarif Semgrep:semgrep.sarif Trivy:trivy.sarif; do
          name=${s%%:*}; file=${s#*:}
          curl -fsS -X POST "$AEGISOPS_URL/api/v1/artifacts/$ARTIFACT_ID/scans" \
            -H "Authorization: Bearer $AEGISOPS_API_KEY" \
            -F scanner=$name -F format=Sarif -F report=@$file
        done
      env:
        AEGISOPS_URL: ${{ vars.AEGISOPS_URL }}
        AEGISOPS_API_KEY: ${{ secrets.AEGISOPS_API_KEY }}
        ARTIFACT_ID: ${{ needs.build.outputs.artifact_id }}
```

Important: scanners run with **exit code 0** in CI. Failing the pipeline on findings is a
policy decision that belongs to AegisOps, otherwise the control plane never sees the
artifact.

## 7. Demo data for scanners

Each target repository has a long-lived branch `demo/vulnerable` (see
[13 Target applications](13-target-applications.md#5-vulnerable-demo-branch)) containing a
fake AWS key, a pinned vulnerable NuGet/npm package and a SQL-concatenation endpoint so that
all three scanners produce findings on demand during demos.

## 8. Dogfooding

AegisOps' own CI runs the same three scanners against the platform (`.github/workflows/ci.yml`),
uploads results as GitHub code-scanning SARIF, and fails on `Critical` — the platform holds
itself to the same bar it enforces.

## 9. Limits and safeguards

| Risk | Control |
| --- | --- |
| Huge reports | 20 MB body limit; async parse; max 10 000 findings per scan (rest summarized) |
| Malicious SARIF (XXE-like, deep nesting) | `System.Text.Json` with `MaxDepth = 64`; no external references |
| Path disclosure in UI | File paths rendered as text only; no links to raw file systems |
| Report tampering by CI | API keys are project-scoped; artifact digest recorded; V2: verify Trivy attestations |
