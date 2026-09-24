# ADR-0010 — SARIF as the normalization format for scanner results

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Three scanners (Gitleaks, Trivy, Semgrep) with three native JSON formats must become one
`SecurityFinding` model with comparable severities so policies can count them uniformly.
Writing and maintaining three bespoke parsers is error-prone and grows with every scanner.

## Decision

- Require **SARIF 2.1.0** as the primary upload format for all scanners (each supports it
  natively).
- Implement one `SarifReportParser` plus small per-tool **enrichers** that read tool-specific
  properties (Trivy CVSS `security-severity`, package/fixed version; Semgrep metadata;
  Gitleaks defaults to Critical).
- Keep the door open for native formats (`TrivyJson`, `GitleaksJson`, `SemgrepJson`) behind
  the same `IReportParser` interface for cases where SARIF loses information.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Native JSON parsers only | Three parsers to maintain; new scanners need new code |
| Let CI pre-normalize into an AegisOps JSON schema | Moves logic into every target workflow; harder to evolve |
| Store raw reports only, count in SQL | Different shapes per tool; no uniform severity |

## Consequences

- Positive: one parser, one severity mapping table, fixture-based tests per tool; adding a
  fourth SARIF-capable scanner needs only an enricher.
- Negative: SARIF severity semantics are loose (`level` vs `security-severity`) → explicit
  mapping table in [07 §4](../07-security-scanning.md#4-normalization-sarif--securityfinding)
  and per-version fixtures.
