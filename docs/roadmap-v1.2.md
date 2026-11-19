# Roadmap — v1.2: connected to the tools teams already use

v1.1 made AegisOps installable on one server ([roadmap-v1.1.md](roadmap-v1.1.md)). v1.2 connects it to GitHub's own
deployment approvals, company sign-in and chat, and lets it deploy beyond its own host. Estimates are working days
for one developer. Every item ships as its own pull request with tests, documentation and a live check.

| # | Item | Why | Estimate |
| --- | --- | --- | --- |
| 6 | **Polish and prove.** Fix the build on the newer .NET SDK (CA2026); pre-release tags that never move `:latest`; release `v1.2.0-rc.1` to exercise the new Docker actions and multi-arch build; re-record the README demo GIF with the new UI. | The repository should look finished while the larger items are built. | 1 |
| 1 | **GitHub deployment protection rule.** A GitHub App whose custom deployment protection rule asks AegisOps for a decision when a workflow job targets a GitHub environment; AegisOps evaluates its policies against the registered artifact and approves or rejects the GitHub deployment with the rule-by-rule explanation. Reachable from GitHub through a Cloudflare Tunnel (or any HTTPS endpoint). | Answers "why not GitHub alone": GitHub keeps the button, AegisOps supplies evidence-based policy. Free for public repositories. | 3 |
| 5 | **Policy parameter forms.** Typed fields per rule type (counts, severities, scanner lists, branch globs, deployment windows) instead of raw JSON, with the same server-side validation. | The largest remaining usability gap in the UI. | 1 |
| 2 | **Single sign-on and MFA.** OpenID Connect sign-in (tested with Keycloak locally; Google and Microsoft Entra documented) mapping groups to roles, and TOTP for local accounts with recovery codes; Admin can require MFA. | The first question a company asks about a tool that controls deployments. | 3 |
| 3 | **Notifications.** Slack incoming webhooks and email (SMTP; Mailpit for local testing) for approval requests, decisions, failed deployments, and temporary passwords sent to the user instead of handed over by an Admin. | Approvers should not have to watch the UI. | 1.5 |
| 4 | **Remote Docker targets.** Deploy to another host over SSH (pinned host key, dedicated key, per-target credentials stored like environment secrets), so AegisOps no longer needs to deploy onto its own host. | Moves beyond the single-machine demo and shrinks the Docker-socket risk. | 3 |

**Order:** 6 → 1 → 5 → 2 → 3 → 4. Items 6, 1 and 5 (about five days) already make a meaningful v1.2.

**Cost:** everything above can be built and tested for free: GitHub Apps and custom deployment protection rules on
public repositories, Cloudflare Tunnel, Keycloak, Google OAuth clients, Slack free workspaces and Mailpit. Microsoft
Teams needs Microsoft 365, so it is left out.

**Not in v1.2:** Kubernetes targets, high availability and multiple organisations, external AI providers (they
change the data-residency model and need a design discussion first), and a hash-chained audit log.
