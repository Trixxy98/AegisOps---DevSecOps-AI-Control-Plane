# ADR-0011 — Policy union with most-restrictive-wins semantics

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Policies can be defined at Global, Team and Project scope. When several apply to one
deployment, the engine must combine them predictably. Two common models exist:
*override* (most specific policy replaces broader ones) and *union* (all apply).

## Decision

- **Union:** all enabled policies whose scope matches (Global ∪ Team ∪ Project) and whose
  `AppliesToTiers` contains the environment tier are evaluated.
- **Most restrictive wins:** `Deny` > `RequireApproval` > `Allow`. Approval quorum is the
  maximum `count` across `RequireApprovals` rules; allowed roles are unioned.
- `Warn` effects never change the decision.
- Missing inputs make a rule fail (**fail closed**). The only fail-open case is "no policy
  configured at all", which is logged as a warning.
- Evaluation is a **pure function** of a fully loaded `PolicyContext`; results and a context
  snapshot are persisted for explainability.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| Override (most specific wins) | A project policy could silently drop the global "no critical findings" rule — the opposite of what a control plane should allow |
| Weighted scoring / risk points | Harder to explain; interviewers and users prefer explicit pass/fail rules |
| OPA / Rego | Powerful, but an external engine hides the logic this project wants to show; a typed rule catalogue with a UI builder is more approachable. Could be added as a `CustomRego` rule type in V2 |

## Consequences

- Positive: safety by construction — scopes can only tighten; explanations are simple lists
  of rule results; exhaustive unit testing is straightforward.
- Negative: teams cannot relax a global rule (by design); exceptions must go through
  suppressions (per finding) or global policy changes, both audited.
