# 01 — Vision & Scope

## 1. One-sentence definition

**AegisOps is a self-hosted DevSecOps & AI control plane: it decides whether a change may
reach an environment and whether a request may reach an AI model, and it records why.**

## 2. The problem

Modern engineering teams have plenty of tools that *do* things — CI systems build and
test, scanners find vulnerabilities, LLMs answer questions. What is usually missing is the
layer that *decides* and *remembers*:

| Without a control plane | With AegisOps |
| --- | --- |
| CI pipelines deploy as long as the YAML says so. Policy lives in scattered `if` blocks. | Policy is data: versioned rules attached to environments, evaluated the same way every time. |
| Scanner results are PDFs nobody reads; a critical CVE can still ship on Friday evening. | Findings are normalized, counted, and become inputs to an `ALLOW / REQUIRE_APPROVAL / DENY` decision. |
| Production approvals happen in chat. Nobody can prove who approved what. | Approvals are first-class records with separation-of-duties enforced. |
| Developers paste customer data and API keys into LLM prompts. | Every AI request is authenticated, rate-limited, scanned for sensitive data, and audited. |
| "Who changed the production policy last month?" → nobody knows. | Append-only audit log for every privileged action. |

## 3. What AegisOps is — and is not

### It is

- A **decision layer** between CI and environments (deployment governance).
- A **gateway** between users/services and LLMs (AI governance).
- A **system of record** for artifacts, scans, policies, approvals, AI usage and audit
  events.
- A **real-time operations UI** for developers, security engineers and approvers.

### It is not

| Not this | Because | Who does it instead |
| --- | --- | --- |
| A CI engine | Building and testing code is a solved problem | GitHub Actions |
| A container orchestrator | v1 targets are single Docker hosts | Docker Engine (v1); Kubernetes is deferred |
| A vulnerability scanner | Scanners are commodity tools | Gitleaks, Trivy, Semgrep |
| An LLM host | Model serving is out of scope | Ollama |
| A SIEM / log platform | Audit log is scoped to AegisOps actions | Grafana/Loki if ever needed |

## 4. Goals

### Product goals (v1)

1. **Explainable deployment decisions.** Every deployment shows which policy rules passed
   or failed and why.
2. **Separation of duties.** Requesters cannot approve their own production deployments.
3. **Security as a gate, not a report.** Scanner findings directly block or escalate
   deployments.
4. **Governed AI access.** Model allow-lists, per-user rate limits, sensitive-data
   detection, full audit — without depending on paid APIs.
5. **Live feedback.** Deployment timelines and approval requests update in real time.
6. **One-command demo.** `docker compose up -d` brings up the entire platform.

### Learning / portfolio goals

The project is built solo and doubles as a portfolio piece. It is intentionally designed
to exercise the parts of ASP.NET Core and platform engineering that interviewers ask about:

- Clean Architecture with enforced dependency rules
- Modular monolith with clear module boundaries
- ASP.NET Core Identity + JWT + **policy-based authorization** (beyond RBAC)
- `BackgroundService` workers with a durable job queue
- EF Core with PostgreSQL (JSONB, schemas, migrations, concurrency)
- Redis for rate limiting and ephemeral state
- SignalR for real-time updates
- SARIF parsing and security tooling integration
- OpenTelemetry-based observability
- Meaningful tests: the policy engine test-suite is the crown jewel, not CRUD tests

## 5. Non-goals (v1)

- Multi-tenancy beyond Teams inside one organisation
- Kubernetes, Helm, GitOps controllers
- Message brokers (Kafka, RabbitMQ)
- Infrastructure provisioning (Terraform, cloud accounts)
- Multiple AI providers, agent frameworks (LangChain), vector databases
- Mobile applications
- High availability / multi-region

These are listed with reasoning in [03 Tech stack](03-tech-stack.md#6-explicitly-excluded-for-v1).

## 6. Personas

| Persona | Role in AegisOps | Typical actions |
| --- | --- | --- |
| **Developer** (Aina) | `Developer` role, member of one or more Teams | Pushes code; CI registers artifacts; requests deployments to Development/Staging freely, Production requires approval; uses the AI chat playground |
| **Security engineer** (Farid) | `Security` role | Defines policies and rules, reviews findings, suppresses false positives, approves production deployments, configures AI policies |
| **Release approver** (Mei) | `Approver` role | Reviews the approval queue, approves/rejects production deployments with a comment |
| **Platform admin** (Rith) | `Admin` role | Manages users, teams, projects, environments, API keys, AI models; reads the audit log |
| **CI bot** (`github-actions[ci]`) | Service principal via project-scoped API key | Registers artifacts, uploads scan reports, requests deployments |

## 7. Core user journeys

### Journey A — A developer ships to production

1. Aina merges to `main` in `circuit-service`.
2. GitHub Actions builds, tests, builds the image, runs Gitleaks/Trivy/Semgrep.
3. CI registers the **Artifact** `v1.8.2` in AegisOps and uploads the three scan reports.
4. CI requests a **Deployment** of `v1.8.2` to **Staging**. The policy engine evaluates:
   tests passed, 0 critical findings, branch allowed → `ALLOW`. The worker deploys. Aina
   watches the timeline update live.
5. Aina requests a deployment of the same artifact to **Production**. The policy engine
   returns `REQUIRE_APPROVAL (2)`. Farid and Mei get a notification, review the findings
   and the staging result, and approve. The worker deploys. Everything is in the audit log.

### Journey B — A vulnerable build is blocked

1. A dependency with a critical CVE lands in `site-service`.
2. Trivy reports 1 `CRITICAL`. The Production policy has `MaxFindings(Critical) = 0`.
3. The deployment request is `DENIED` with the message
   *"Rule MaxFindings failed: 1 CRITICAL finding (CVE-2026-XXXXX in libssl) exceeds limit 0."*
4. Nobody can override the denial through the UI; the fix must ship as a new artifact.

### Journey C — Governed AI usage

1. Aina pastes a stack trace containing an AWS access key into the AI playground.
2. The AI gateway authenticates her, checks her team's AI policy (model allowed), checks
   the Redis rate limit (7/20 this minute), scans the prompt and detects `AWS_ACCESS_KEY`.
3. Policy says `Redact`: the key is replaced with `[REDACTED:AWS_ACCESS_KEY]` and the
   prompt is forwarded to Ollama. The **AiRequest** record stores what was detected and
   that redaction happened.
4. Farid later reviews AI requests filtered by `detectedSensitiveTypes`.

### Journey D — Security engineer tightens policy

1. Farid edits the Production policy and adds `DeploymentWindow` (no deployments Fri 18:00
   → Mon 08:00 MYT).
2. The policy version increments; the change is audited.
3. A Friday-night deployment request now returns `DENY` with the window explanation.

## 8. Success criteria

### For the product

- [ ] Cold start to running platform in ≤ 5 minutes with `docker compose up -d`
- [ ] All four journeys above are reproducible with seeded demo data
- [ ] Every deployment decision is explainable rule-by-rule in the UI and via API
- [ ] 100% of privileged actions produce an audit event
- [ ] Policy engine has ≥ 95% branch coverage and a documented test matrix

### For the portfolio

- [ ] README with architecture diagram, 60-second demo GIF and "how to run"
- [ ] ADRs explaining every major technology decision
- [ ] Dogfooding: AegisOps' own CI runs the same three scanners
- [ ] Clear interview narrative: *"CI does the work; AegisOps makes and records the decision."*

## 9. Constraints

| Constraint | Implication |
| --- | --- |
| Solo developer, part-time | Ruthless scope control; phases must each deliver something demonstrable |
| 100% free tooling | Local LLM via Ollama; no paid SaaS; GitHub free tier |
| Runs on a laptop | Docker Compose, not Kubernetes; small LLM models (3B–8B) |
| Interview-ready | Prefer depth in a few areas over breadth in many |
