# 20 — Glossary

| Term | Meaning in AegisOps |
| --- | --- |
| **AegisOps** | The control plane itself: API, Worker and Web. Named after *aegis* (shield/protection) + *Ops*. |
| **Control plane** | The component that makes and records decisions (deploy or not, allow AI request or not) as opposed to the *data plane* (CI runners, containers, LLM) that does the work. |
| **Target application / target service** | A service governed by AegisOps (`site-service`, `circuit-service`, `notification-service`). Not part of the AegisOps codebase. |
| **Team** | Ownership unit; owns Projects; scope for AI policies. |
| **Project** | One deployable service, belonging to one Team, with Environments and a Repository. |
| **Environment** | A deployment destination of a Project with a `Tier` (`Development`, `Staging`, `Production`) and an executor `Target`. |
| **Tier** | The kind of environment; policies attach to tiers. |
| **Target (environment)** | JSON describing where/how the executor deploys (`docker` or `noop`). |
| **Repository** | Source repository metadata for a Project (informational in v1). |
| **Artifact** | An immutable, deployable build of a Project: version + commit + container image (+ digest) + build/test status. |
| **Security scan** | One scanner's run against an Artifact (Gitleaks, Trivy or Semgrep), with normalized findings and a severity summary. |
| **Finding** | A single issue reported by a scanner (secret, CVE, code pattern). |
| **Fingerprint** | Stable hash identifying "the same" finding across artifacts; used for suppressions. |
| **Suppression** | A `Security`/`Admin` decision that a finding (by fingerprint, per project) is a false positive or accepted risk; excluded from policy counts. |
| **SARIF** | Static Analysis Results Interchange Format (OASIS). The common report format AegisOps ingests. |
| **Policy** | A named, versioned set of rules attached to a scope (Global/Team/Project) and tiers. |
| **Policy rule** | One typed check with parameters and an effect. |
| **Effect** | What a failing rule contributes: `Deny`, `RequireApproval` or `Warn`. |
| **Decision** | Result of evaluating all applicable policies: `Allow`, `RequireApproval(n)` or `Deny`. |
| **Policy evaluation** | Persisted record of a decision with rule-by-rule results and a frozen context snapshot. |
| **Policy engine** | The pure component that produces a Decision from a `PolicyContext`. |
| **Policy context** | All inputs to an evaluation: artifact, scans, environment, requester, approvals, prior environment, time. |
| **Deployment** | A request to place an Artifact into an Environment; goes through the state machine `Requested → … → Succeeded/Failed/Denied/Rejected/Cancelled`. |
| **Deployment event** | An entry in a deployment's timeline. |
| **Approval** | A recorded `Approved`/`Rejected` decision by an eligible user on a deployment awaiting approval. |
| **Quorum** | Number of valid approvals required (`ApprovalsRequired`). |
| **Separation of duties** | The requester of a deployment may not approve it. |
| **Executor** | Implementation of `IDeploymentExecutor` that performs a deployment (`noop`, `docker`). |
| **Rollback** | A new deployment of the previously successful artifact; also the executor's automatic restore on health-check failure. |
| **Job** | A durable unit of background work stored in `jobs.jobs` and processed by the Worker. |
| **Worker** | The `AegisOps.Worker` process hosting `BackgroundService`s that process jobs. |
| **Dead letter** | A job that exhausted its retries and needs a human. |
| **API key** | A project-scoped credential (`aok_…`) used by CI to talk to AegisOps. |
| **Permission** | Fine-grained capability (`deployments:approve`) derived from roles or API-key scopes. |
| **Policy-based authorization** | ASP.NET Core mechanism where endpoints declare requirements and handlers evaluate them against the principal and resource. Distinct from the *policy engine*. |
| **AI gateway** | The governed path from a user/feature to an LLM: authz → AI policy → rate limit → validation → sensitive-data detection → model → audit. |
| **AI policy** | Rules for AI usage per Team or Global: allowed models, rate limits, prompt limits, sensitive-data action, retention. |
| **AI request** | Audit record of one gateway call, including blocked ones. |
| **Sensitive-data detection** | Regex-based identification of secrets/PII in prompts; action `Allow`/`Redact`/`Block`. |
| **Redaction** | Replacing a detected span with `[REDACTED:<TYPE>]` before the prompt leaves AegisOps. |
| **Ollama** | Local LLM server used as the only AI provider in v1. |
| **Audit event** | Append-only record of who did what to which resource, with outcome and correlation id. |
| **Correlation id** | Identifier propagated from request → job → events → logs to reconstruct a flow. |
| **Idempotency key** | Client-supplied header ensuring repeated `POST`s create one resource. |
| **Modular monolith** | One deployable with internal modules that only talk through defined interfaces. |
| **Clean Architecture** | Layering where dependencies point inward: Domain ← Application ← Infrastructure/Api/Worker. |
| **Dogfooding** | AegisOps' own CI applies the same scanners and thresholds it enforces on targets. |
| **Demo/vulnerable branch** | Intentionally insecure branch in each target repo used to trigger findings in demos. |
| **MYT** | Malaysia Time, `Asia/Kuala_Lumpur` (UTC+8), used in deployment-window examples. |
