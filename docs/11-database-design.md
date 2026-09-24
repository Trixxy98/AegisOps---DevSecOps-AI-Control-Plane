# 11 — Database Design

PostgreSQL 17, one database `aegisops`, one EF Core `AegisOpsDbContext`, **one schema per
module**. Naming: `snake_case` tables/columns (via `EFCore.NamingConventions`), plural table
names, `<table>_pkey`, `ix_<table>_<cols>`, `fk_<table>_<ref>`.

## 1. Schemas

| Schema | Module | Tables |
| --- | --- | --- |
| `identity` | Identity & Access | `users`, `roles`, `user_roles`, `user_claims`, `role_claims`, `user_logins`, `user_tokens` (Identity), `refresh_tokens`, `api_keys` |
| `org` | Organization | `teams`, `team_members`, `projects`, `repositories`, `environments` |
| `security` | Artifacts & Security | `artifacts`, `security_scans`, `security_findings`, `finding_suppressions` |
| `policy` | Policies | `policies`, `policy_rules`, `policy_evaluations` |
| `deploy` | Deployments | `deployments`, `deployment_events`, `approvals` |
| `ai` | AI Gateway | `ai_models`, `ai_policies`, `ai_requests` |
| `audit` | Audit | `audit_events` |
| `jobs` | Jobs | `jobs` |

Cross-schema foreign keys are allowed (same database) but only **toward** owning modules
(e.g. `deploy.deployments.artifact_id → security.artifacts.id`), never in cycles.

## 2. Column conventions

| Concept | Type |
| --- | --- |
| Primary keys | `uuid` (GUID v7 generated in app) |
| Timestamps | `timestamptz`, column names `*_at` |
| Enums | `text` with CHECK constraint generated from C# enum names (readable in SQL, no migration on reorder) |
| JSON payloads | `jsonb` |
| Money/none | — |
| Soft delete | Only where stated (`policies.archived_at`); everything else is hard-delete-free by design (audit, events) |
| Concurrency | `xmin` system column mapped as `RowVersion` on `deployments`, `policies`, `environments` |

## 3. Tables (abridged DDL)

### `org`

```sql
CREATE TABLE org.teams (
  id uuid PRIMARY KEY, name text NOT NULL, slug text NOT NULL UNIQUE,
  description text, webhook_url text, webhook_secret_hash text, created_at timestamptz NOT NULL);

CREATE TABLE org.team_members (
  team_id uuid REFERENCES org.teams, user_id uuid REFERENCES identity.users,
  role text NOT NULL CHECK (role IN ('Owner','Member')), joined_at timestamptz NOT NULL,
  PRIMARY KEY (team_id, user_id));

CREATE TABLE org.projects (
  id uuid PRIMARY KEY, team_id uuid NOT NULL REFERENCES org.teams, name text NOT NULL,
  slug text NOT NULL UNIQUE, description text, is_archived boolean NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL);

CREATE TABLE org.repositories (
  id uuid PRIMARY KEY, project_id uuid NOT NULL UNIQUE REFERENCES org.projects,
  provider text NOT NULL, full_name text NOT NULL, default_branch text NOT NULL, html_url text);

CREATE TABLE org.environments (
  id uuid PRIMARY KEY, project_id uuid NOT NULL REFERENCES org.projects, name text NOT NULL,
  tier text NOT NULL CHECK (tier IN ('Development','Staging','Production')), "order" int NOT NULL,
  target jsonb NOT NULL DEFAULT '{"type":"noop"}', current_artifact_id uuid, last_deployment_id uuid,
  UNIQUE (project_id, name));
```

### `security`

```sql
CREATE TABLE security.artifacts (
  id uuid PRIMARY KEY, project_id uuid NOT NULL REFERENCES org.projects, version text NOT NULL,
  commit_sha text NOT NULL, branch text NOT NULL, image_reference text NOT NULL, image_digest text,
  ci_provider text, ci_run_id text, ci_run_url text,
  build_status text NOT NULL, test_status text NOT NULL, test_summary jsonb,
  created_by_id uuid, created_by_api_key_id uuid, created_at timestamptz NOT NULL,
  UNIQUE (project_id, version));
CREATE INDEX ix_artifacts_project_created ON security.artifacts (project_id, created_at DESC);

CREATE TABLE security.security_scans (
  id uuid PRIMARY KEY, artifact_id uuid NOT NULL REFERENCES security.artifacts ON DELETE CASCADE,
  scanner text NOT NULL, source text NOT NULL, status text NOT NULL, report_format text NOT NULL,
  tool_version text, started_at timestamptz, completed_at timestamptz,
  summary jsonb NOT NULL DEFAULT '{}', raw_report_path text, error_message text,
  UNIQUE (artifact_id, scanner));

CREATE TABLE security.security_findings (
  id uuid PRIMARY KEY, scan_id uuid NOT NULL REFERENCES security.security_scans ON DELETE CASCADE,
  artifact_id uuid NOT NULL, severity text NOT NULL, rule_id text NOT NULL, title text NOT NULL,
  description text, file_path text, start_line int, package_name text, installed_version text,
  fixed_version text, cve text, fingerprint text NOT NULL, status text NOT NULL DEFAULT 'Open',
  suppressed_by_id uuid, suppression_reason text, help_uri text);
CREATE INDEX ix_findings_scan_severity ON security.security_findings (scan_id, severity);
CREATE INDEX ix_findings_artifact_status ON security.security_findings (artifact_id, status);
CREATE INDEX ix_findings_fingerprint ON security.security_findings (fingerprint);

CREATE TABLE security.finding_suppressions (
  id uuid PRIMARY KEY, project_id uuid NOT NULL REFERENCES org.projects, fingerprint text NOT NULL,
  reason text NOT NULL, created_by_id uuid NOT NULL, created_at timestamptz NOT NULL, expires_at timestamptz,
  UNIQUE (project_id, fingerprint));
```

### `policy`

```sql
CREATE TABLE policy.policies (
  id uuid PRIMARY KEY, name text NOT NULL, description text, scope text NOT NULL, scope_id uuid,
  applies_to_tiers text[] NOT NULL, is_enabled boolean NOT NULL DEFAULT true, version int NOT NULL DEFAULT 1,
  created_by_id uuid NOT NULL, updated_by_id uuid NOT NULL, created_at timestamptz NOT NULL,
  updated_at timestamptz NOT NULL, archived_at timestamptz);
CREATE INDEX ix_policies_scope ON policy.policies (scope, scope_id) WHERE archived_at IS NULL;

CREATE TABLE policy.policy_rules (
  id uuid PRIMARY KEY, policy_id uuid NOT NULL REFERENCES policy.policies ON DELETE CASCADE,
  type text NOT NULL, effect text NOT NULL, parameters jsonb NOT NULL DEFAULT '{}',
  "order" int NOT NULL, is_enabled boolean NOT NULL DEFAULT true);

CREATE TABLE policy.policy_evaluations (
  id uuid PRIMARY KEY, deployment_id uuid NOT NULL, decision text NOT NULL, approvals_required int NOT NULL,
  evaluated_at timestamptz NOT NULL, evaluated_policies jsonb NOT NULL, rule_results jsonb NOT NULL,
  context_snapshot jsonb NOT NULL);
CREATE INDEX ix_policy_evaluations_deployment ON policy.policy_evaluations (deployment_id, evaluated_at DESC);
```

A deployment can have several evaluations over time (request, after approvals, pre-execution);
`deployments.policy_evaluation_id` points to the latest.

### `deploy`

```sql
CREATE TABLE deploy.deployments (
  id uuid PRIMARY KEY, project_id uuid NOT NULL REFERENCES org.projects,
  environment_id uuid NOT NULL REFERENCES org.environments, artifact_id uuid NOT NULL REFERENCES security.artifacts,
  status text NOT NULL, requested_by_id uuid, requested_by_api_key_id uuid, requested_at timestamptz NOT NULL,
  reason text, policy_evaluation_id uuid, approvals_required int NOT NULL DEFAULT 0,
  approvals_received int NOT NULL DEFAULT 0, started_at timestamptz, completed_at timestamptz,
  failure_reason text, previous_artifact_id uuid, correlation_id text NOT NULL);
CREATE INDEX ix_deployments_project_requested ON deploy.deployments (project_id, requested_at DESC);
CREATE INDEX ix_deployments_env_status ON deploy.deployments (environment_id, status);
CREATE INDEX ix_deployments_status_awaiting ON deploy.deployments (requested_at) WHERE status = 'AwaitingApproval';

CREATE TABLE deploy.deployment_events (
  id uuid PRIMARY KEY, deployment_id uuid NOT NULL REFERENCES deploy.deployments ON DELETE CASCADE,
  sequence int NOT NULL, "timestamp" timestamptz NOT NULL, type text NOT NULL, message text NOT NULL,
  data jsonb, UNIQUE (deployment_id, sequence));

CREATE TABLE deploy.approvals (
  id uuid PRIMARY KEY, deployment_id uuid NOT NULL REFERENCES deploy.deployments ON DELETE CASCADE,
  approver_id uuid NOT NULL REFERENCES identity.users, decision text NOT NULL, comment text,
  decided_at timestamptz NOT NULL, UNIQUE (deployment_id, approver_id));
```

### `ai`

```sql
CREATE TABLE ai.ai_models (
  id uuid PRIMARY KEY, name text NOT NULL UNIQUE, provider text NOT NULL, display_name text NOT NULL,
  is_enabled boolean NOT NULL DEFAULT true, max_context_tokens int, capabilities text[] NOT NULL DEFAULT '{}');

CREATE TABLE ai.ai_policies (
  id uuid PRIMARY KEY, name text NOT NULL, scope text NOT NULL, scope_id uuid, allowed_model_ids uuid[] NOT NULL,
  requests_per_minute int NOT NULL, requests_per_day int NOT NULL, max_prompt_chars int NOT NULL,
  sensitive_data_action text NOT NULL, blocked_patterns text[] NOT NULL DEFAULT '{}',
  redact_pii boolean NOT NULL DEFAULT false, store_content boolean NOT NULL DEFAULT false,
  is_enabled boolean NOT NULL DEFAULT true, version int NOT NULL DEFAULT 1,
  updated_by_id uuid NOT NULL, updated_at timestamptz NOT NULL);
CREATE UNIQUE INDEX ux_ai_policies_scope ON ai.ai_policies (scope, scope_id) WHERE is_enabled;

CREATE TABLE ai.ai_requests (
  id uuid PRIMARY KEY, user_id uuid NOT NULL, team_id uuid, ai_model_id uuid, ai_policy_id uuid,
  status text NOT NULL, purpose text NOT NULL, prompt_chars int NOT NULL, response_chars int,
  prompt_tokens int, completion_tokens int, latency_ms int, detected_sensitive_types text[] NOT NULL DEFAULT '{}',
  redactions_applied int NOT NULL DEFAULT 0, block_reason text, prompt_hash text NOT NULL,
  prompt_preview text, response_preview text, prompt_content text, response_content text,
  correlation_id text NOT NULL, created_at timestamptz NOT NULL);
CREATE INDEX ix_ai_requests_user_created ON ai.ai_requests (user_id, created_at DESC);
CREATE INDEX ix_ai_requests_created ON ai.ai_requests (created_at DESC);
CREATE INDEX ix_ai_requests_detected ON ai.ai_requests USING gin (detected_sensitive_types);
```

### `audit`

```sql
CREATE TABLE audit.audit_events (
  id uuid PRIMARY KEY, "timestamp" timestamptz NOT NULL, actor_type text NOT NULL, actor_id uuid,
  actor_display text NOT NULL, action text NOT NULL, resource_type text NOT NULL, resource_id text,
  outcome text NOT NULL, ip_address inet, user_agent text, correlation_id text NOT NULL,
  metadata jsonb NOT NULL DEFAULT '{}');
CREATE INDEX ix_audit_timestamp ON audit.audit_events ("timestamp" DESC);
CREATE INDEX ix_audit_resource ON audit.audit_events (resource_type, resource_id, "timestamp" DESC);
CREATE INDEX ix_audit_actor ON audit.audit_events (actor_id, "timestamp" DESC);
CREATE INDEX ix_audit_action ON audit.audit_events (action);
```

Immutability: the application role `aegisops_app` has `INSERT, SELECT` only on `audit.*`.
Migrations run as a separate role `aegisops_migrator` (owner). V2: monthly partitioning by
`timestamp`.

### `jobs`

```sql
CREATE TABLE jobs.jobs (
  id uuid PRIMARY KEY, type text NOT NULL, payload jsonb NOT NULL, status text NOT NULL DEFAULT 'Pending',
  attempts int NOT NULL DEFAULT 0, max_attempts int NOT NULL DEFAULT 5, scheduled_at timestamptz NOT NULL,
  locked_at timestamptz, locked_by text, completed_at timestamptz, last_error text,
  correlation_id text NOT NULL, created_at timestamptz NOT NULL);
CREATE INDEX ix_jobs_pending ON jobs.jobs (scheduled_at) WHERE status = 'Pending';
CREATE INDEX ix_jobs_running ON jobs.jobs (locked_at) WHERE status = 'Running';
```

Retention: `Succeeded` jobs deleted after 7 days by a nightly cleanup job; `DeadLettered`
kept until manually resolved (`POST /admin/jobs/{id}/retry`, Admin-only, Phase 4).

### `identity` additions

```sql
CREATE TABLE identity.refresh_tokens (
  id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES identity.users ON DELETE CASCADE,
  token_hash text NOT NULL UNIQUE, family_id uuid NOT NULL, expires_at timestamptz NOT NULL,
  revoked_at timestamptz, replaced_by_token_id uuid, created_at timestamptz NOT NULL, created_by_ip inet);

CREATE TABLE identity.api_keys (
  id uuid PRIMARY KEY, project_id uuid NOT NULL REFERENCES org.projects ON DELETE CASCADE, name text NOT NULL,
  key_prefix text NOT NULL UNIQUE, key_hash text NOT NULL, scopes text[] NOT NULL,
  expires_at timestamptz NOT NULL, last_used_at timestamptz, revoked_at timestamptz,
  created_by_id uuid NOT NULL, created_at timestamptz NOT NULL);
```

## 4. EF Core practices

| Topic | Practice |
| --- | --- |
| Configuration | One `IEntityTypeConfiguration<T>` per entity under `Infrastructure/Persistence/Configurations/<Module>/` |
| JSONB | `OwnsOne(...).ToJson()` for structured value objects (`TestSummary`, `ScanSummary`, `Target`); `jsonb` column with `JsonDocument`/typed POCO via `HasColumnType("jsonb")` for free-form (`RuleResults`) |
| Enums | `.HasConversion<string>()` + check constraint via `HasCheckConstraint` |
| Arrays | Npgsql native `text[]`/`uuid[]` mapping |
| Concurrency | `.UseXminAsConcurrencyToken()` |
| Queries | `AsNoTracking()` for reads; projections to DTOs in read handlers; no lazy loading |
| Migrations | Generated in `AegisOps.Infrastructure`, applied by a dedicated `AegisOps.Api --migrate` startup flag in Compose (init container), never automatically in production runs |
| Seeding | `DemoSeeder` (idempotent) runs when `Seed:Demo = true`: users, one team, three projects with environments, policies from [05 §7](05-policy-engine.md#7-worked-examples), AI models/policy, sample artifacts/scans/deployments |
| Naming | `UseSnakeCaseNamingConvention()` |
| Testing | Testcontainers PostgreSQL; `Respawn` to reset between tests |

### Migration commands

```bash
# add migration (from repo root)
dotnet ef migrations add <Name> --project src/AegisOps.Infrastructure --startup-project src/AegisOps.Api --output-dir Persistence/Migrations
# apply
dotnet ef database update --project src/AegisOps.Infrastructure --startup-project src/AegisOps.Api
# generate idempotent script for review
dotnet ef migrations script --idempotent --project src/AegisOps.Infrastructure --startup-project src/AegisOps.Api -o deploy/sql/migrate.sql
```

Every migration is reviewed to contain only the intended changes (generators occasionally
emit noise for JSON/array columns).

## 5. Data volume assumptions (portfolio scale)

| Table | Expected rows | Notes |
| --- | --- | --- |
| deployments | thousands | fine without partitioning |
| security_findings | 10⁵ | indexes above sufficient; consider `pg_trgm` for search in V2 |
| audit_events | 10⁵–10⁶ | partition by month in V2 |
| ai_requests | 10⁴–10⁵ | GIN on `detected_sensitive_types` for security review filters |
| jobs | small, self-cleaning | |

## 6. Backup & restore (demo)

`scripts/db-backup.sh` → `pg_dump -Fc` into `./backups/`; `scripts/db-restore.sh`. Reports
directory backed up alongside. Documented in the README for completeness; no scheduled
backups in v1.
