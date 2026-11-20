# Roadmap — v1.2: connected to the tools teams already use

v1.1 made AegisOps installable on one server ([roadmap-v1.1.md](roadmap-v1.1.md)). v1.2 connects it to GitHub's own
deployment approvals, company sign-in and chat, and lets it deploy beyond its own host. Estimates are working days
for one developer. Every item ships as its own pull request with tests, documentation and a live check.

| # | Item | Why | Estimate |
| --- | --- | --- | --- |
| 6 | **Polish and prove.** Fix the build on the newer .NET SDK (CA2026); pre-release tags that never move `:latest`; release `v1.2.0-rc.1` to exercise the new Docker actions and multi-arch build; re-record the README demo GIF with the new UI. | The repository should look finished while the larger items are built. | 1 |
| 1 | **Done and verified live.** **GitHub deployment protection rule.** A GitHub App whose custom deployment protection rule asks AegisOps for a decision when a workflow job targets a GitHub environment; AegisOps evaluates its policies against the registered artifact and approves or rejects the GitHub deployment with the rule-by-rule explanation. Reachable from GitHub through a Cloudflare Tunnel (or any HTTPS endpoint). | Answers "why not GitHub alone": GitHub keeps the button, AegisOps supplies evidence-based policy. Free for public repositories. | 3 |
| 5 | **Done.** **Policy parameter forms.** Typed fields per rule type (counts, severities, scanner lists, branch globs, deployment windows) instead of raw JSON, with the same server-side validation. | The largest remaining usability gap in the UI. | 1 |
| 2 | **Single sign-on and MFA.** OpenID Connect sign-in (tested with Keycloak locally; Google and Microsoft Entra documented) mapping groups to roles, and TOTP for local accounts with recovery codes; Admin can require MFA. | The first question a company asks about a tool that controls deployments. | 3 |
| 3 | **Notifications.** Slack incoming webhooks and email (SMTP; Mailpit for local testing) for approval requests, decisions, failed deployments, and temporary passwords sent to the user instead of handed over by an Admin. | Approvers should not have to watch the UI. | 1.5 |
| 4 | **Remote Docker targets.** Deploy to another host over SSH (pinned host key, dedicated key, per-target credentials stored like environment secrets), so AegisOps no longer needs to deploy onto its own host. | Moves beyond the single-machine demo and shrinks the Docker-socket risk. | 3 |

**Order:** 6 → 1 → 5 → 2 → 3 → 4. Items 6, 1 and 5 (about five days) already make a meaningful v1.2.

**Cost:** everything above can be built and tested for free: GitHub Apps and custom deployment protection rules on
public repositories, Cloudflare Tunnel, Keycloak, Google OAuth clients, Slack free workspaces and Mailpit. Microsoft
Teams needs Microsoft 365, so it is left out.

**Not in v1.2:** Kubernetes targets, high availability and multiple organisations, external AI providers (they
change the data-residency model and need a design discussion first), and a hash-chained audit log.

## Status

**Item 1 — GitHub deployment protection rule: implemented.** Webhook `POST /api/v1/integrations/github/webhook`
(HMAC-verified, idempotent, durable jobs), Worker mapping (repository → project, environment name → environment,
commit → artifact), the `github` target type, approved/rejected callbacks with retries, the
`deploy.github_deployment_gates` table (migration `GitHubDeploymentGates`), a GitHub badge and run link in the UI,
and the guide [github-deployment-gate.md](github-deployment-gate.md). Verified with xUnit (stubbed GitHub, runtime
RSA key) and Playwright (mocked API). **Verified live on 2026-11-20:** GitHub App *AegisOps Gate* on `Trixxy98/aegisops-circuit-service`, reached through a named Cloudflare Tunnel that publishes only the webhook path; signed ping 204, unsigned/wrong signature 401; a Production run held by GitHub, *Require approval (2)* in AegisOps, two approvals, review callback 204 and the `deploy-production` job ran (*AegisOps Gate — passed*); a run with 11 open Critical findings was rejected on
GitHub with the failed rule (*AegisOps Gate — failed*, `MaxFindings [Deny]`); the PostgreSQL-gated job loop with the new job types runs only when `AEGISOPS_TEST_POSTGRES` is set.
repository, reached through a Cloudflare Tunnel, approving one run (Allow), one after an AegisOps approval
(RequireApproval) and rejecting one (Deny), observed on the GitHub run page and in the AegisOps timeline and audit
log; the PostgreSQL-gated job loop has not been run with the new job types against a real database.

**Item 5 — Policy parameter forms: implemented.** The Policies page edits every rule type with a typed form
(number fields with ranges, severity/scanner/tier selects, scanner and approver-role checkboxes, a branch-glob list
editor, and a deployment-window editor with an IANA time zone select and day/from/until rows), shows one
plain-language sentence per rule with its current values, has per-rule Save/Cancel driven by a dirty check, shows
Problem Details on the rule, and keeps an "Advanced: edit JSON" escape hatch that opens automatically (with a notice)
for stored parameters the form cannot represent. The API validates every rule type on policy create and replace
(`PolicyRuleParameters`, ranges in [05 §4](05-policy-engine.md#parameter-reference-and-validation)), naming the rule
and field in a 400 Problem Details and storing nothing; existing stored rules still load and evaluate (fail closed,
including an unresolvable deployment-window time zone). Verified with xUnit (valid/invalid cases per type, an API
test that a rejected replace leaves the stored rules and version unchanged) and Playwright (each form's exact PUT
body, dirty state, JSON escape hatch, server error display, visual tour in both themes). Not yet checked against the
live Compose stack.
