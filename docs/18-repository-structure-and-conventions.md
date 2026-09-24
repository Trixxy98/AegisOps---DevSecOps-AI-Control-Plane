# 18 — Repository Structure & Conventions

## 1. Repository layout (AegisOps platform)

```
AegisOps/
├── .github/
│   ├── workflows/
│   │   ├── ci.yml                     # backend + frontend + security + images
│   │   ├── release.yml                # on tag v*
│   │   └── codeql.yml                 # optional
│   ├── actions/aegisops-report/       # composite action used by target repos
│   ├── dependabot.yml
│   ├── PULL_REQUEST_TEMPLATE.md
│   └── ISSUE_TEMPLATE/
├── docs/                              # this documentation (+ adr/)
├── src/
│   ├── AegisOps.Domain/
│   │   ├── Common/                    # Entity, AggregateRoot, IDomainEvent, Result, Guard
│   │   ├── Identity/                  # User (partial), ApiKey, RefreshToken
│   │   ├── Organization/              # Team, TeamMember, Project, Environment, Repository, DeploymentTarget (VO)
│   │   ├── Security/                  # Artifact, SecurityScan, SecurityFinding, Severity, ScanSummary
│   │   ├── Policies/                  # Policy, PolicyRule, PolicyEvaluation, PolicyContext, Rules/*, PolicyEngine
│   │   ├── Deployments/               # Deployment, DeploymentEvent, Approval, DeploymentStatus, transitions
│   │   ├── Ai/                        # AiModel, AiPolicy, AiRequest, SensitiveDataType
│   │   ├── Audit/                     # AuditEvent
│   │   └── Jobs/                      # Job, JobType
│   ├── AegisOps.Application/
│   │   ├── Common/                    # ICurrentUser, IUnitOfWork, IClock (TimeProvider), Paging, behaviours
│   │   ├── Abstractions/              # ports: IAegisOpsDbContext, ILlmClient, IDeploymentExecutor, IJobQueue, IRealtimePublisher, IReportStorage, IAiRateLimiter, IAuditWriter
│   │   ├── Identity/                  # Login, Refresh, CreateApiKey…  (one folder per use case: Command/Query, Handler, Validator, Response)
│   │   ├── Organization/
│   │   ├── Security/                  # RegisterArtifact, IngestScanReport, SuppressFinding, queries
│   │   ├── Policies/                  # CreatePolicy, UpdatePolicy, SimulateEvaluation, PolicyContextBuilder
│   │   ├── Deployments/               # RequestDeployment, AddApproval, CancelDeployment, Rollback, queries
│   │   ├── Ai/                        # ChatCompletion (gateway pipeline), SummarizeDeployment, ExplainFinding
│   │   ├── Audit/
│   │   ├── Jobs/                      # IJobHandler<T>, handlers: EvaluateDeployment, ExecuteDeployment, ParseScanReport…
│   │   └── Authorization/             # Permissions, RolePermissions, requirements, Operations
│   ├── AegisOps.Infrastructure/
│   │   ├── Persistence/               # AegisOpsDbContext, Configurations/<Module>/, Migrations/, Seeding/, Interceptors (audit/timestamps)
│   │   ├── Identity/                  # Identity stores, TokenService, ApiKeyAuthenticationHandler
│   │   ├── Redis/                     # RateLimiter (Lua), IdempotencyStore, DistributedLock, RealtimePublisher/Subscriber
│   │   ├── Scanning/                  # SarifReportParser, TrivyEnricher, SemgrepEnricher, GitleaksEnricher, ReportStorage
│   │   ├── Docker/                    # DockerDeploymentExecutor, NoopDeploymentExecutor, DockerClientFactory
│   │   ├── Ai/                        # OllamaLlmClient, RegexSensitiveDataDetector, Detectors/*
│   │   ├── Jobs/                      # PostgresJobQueue
│   │   └── DependencyInjection.cs
│   ├── AegisOps.Api/
│   │   ├── Endpoints/<Module>/        # one static class per endpoint (MapXxx + Handle)
│   │   ├── Realtime/                  # OpsHub, RealtimeSubscriberService, group authorization
│   │   ├── Middleware/                # CorrelationId, ProblemDetails mapping, security headers
│   │   ├── Filters/                   # ValidationFilter, IdempotencyFilter
│   │   ├── Extensions/                # AddApiServices, UseApiPipeline
│   │   ├── Program.cs                 # composition root; supports --migrate --seed
│   │   ├── appsettings*.json
│   │   └── Dockerfile
│   ├── AegisOps.Worker/
│   │   ├── Services/                  # JobDispatcherService, StaleJobReaperService, QueueDepthMetricsService
│   │   ├── Program.cs
│   │   └── Dockerfile
│   └── AegisOps.Web/                  # React (see 12-frontend.md)
├── tests/
│   ├── AegisOps.Domain.Tests/
│   ├── AegisOps.Application.Tests/
│   ├── AegisOps.Architecture.Tests/
│   ├── AegisOps.Infrastructure.Tests/
│   ├── AegisOps.Api.IntegrationTests/
│   └── fixtures/                      # sarif/, ci-payloads/
├── deploy/
│   ├── docker-compose.targets.yml
│   ├── web/nginx.conf
│   ├── prometheus/{prometheus.yml,rules.yml}
│   ├── grafana/{provisioning/,dashboards/}
│   └── sql/                           # generated idempotent migration scripts, role setup
├── scripts/
├── AegisOps.slnx
├── Directory.Build.props              # nullable, implicit usings, warnings-as-errors, analyzers
├── Directory.Packages.props           # central package versions
├── .editorconfig
├── .gitignore
├── .gitleaks.toml
├── .semgrepignore
├── docker-compose.yml
├── docker-compose.override.yml
├── .env.example
├── CHANGELOG.md
├── LICENSE
└── README.md
```

## 2. C# conventions

| Topic | Rule |
| --- | --- |
| Language | Latest C# with .NET 10; `Nullable` enabled; `ImplicitUsings` enabled; `TreatWarningsAsErrors` true |
| Analyzers | `Microsoft.CodeAnalysis.NetAnalyzers` (built-in) at `AnalysisLevel=latest-recommended`; `SonarAnalyzer.CSharp` optional |
| Namespaces | File-scoped; match folder path (`AegisOps.Domain.Policies.Rules`) |
| Types | `sealed` by default; `record` for DTOs/value objects/commands; classes for entities |
| Entities | Private setters; behaviour methods (`TransitionTo`, `AddApproval`); factory methods for creation; no public parameterless ctor except for EF (private) |
| Use cases | `XxxCommand`/`XxxQuery` record + `XxxHandler` class with `HandleAsync(cmd, ct)`; one folder per use case; registered via assembly scanning |
| Results | Domain rule violations throw typed exceptions (`DomainException` subclasses) mapped to Problem Details; expected alternatives (not found) use `Result<T>` |
| Async | `Async` suffix; always pass `CancellationToken`; no `.Result`/`.Wait()` |
| DI | Constructor injection; no service locator; `IOptions<T>` for config |
| Time | `TimeProvider` only — never `DateTime.UtcNow` (analyzer rule banned API list) |
| IDs | `Guid.CreateVersion7()` |
| Logging | `ILogger<T>` with message templates; `LoggerMessage` source generators for hot paths |
| Mapping | Hand-written `ToResponse()` extension methods per module |
| Validation | One `AbstractValidator<T>` per command/query, colocated |
| Tests | See [16](16-testing-strategy.md); naming `Method_Scenario_Expected` |
| Formatting | `dotnet format` enforced in CI; `.editorconfig` committed |

### Endpoint template

```csharp
namespace AegisOps.Api.Endpoints.Deployments;

public static class RequestDeployment
{
    public static RouteGroupBuilder Map(this RouteGroupBuilder group)
    {
        group.MapPost("/deployments", Handle)
             .WithName("RequestDeployment")
             .WithSummary("Request a deployment of an artifact to an environment")
             .RequireAuthorization(AuthPolicies.DeploymentsRequest)
             .AddEndpointFilter<ValidationFilter<RequestDeploymentCommand>>()
             .AddEndpointFilter<IdempotencyFilter>()
             .Produces<DeploymentResponse>(StatusCodes.Status202Accepted)
             .ProducesValidationProblem()
             .ProducesProblem(StatusCodes.Status409Conflict);
        return group;
    }

    private static async Task<IResult> Handle(
        RequestDeploymentCommand command, RequestDeploymentHandler handler, CancellationToken ct)
    {
        var response = await handler.HandleAsync(command, ct);
        return TypedResults.Accepted($"/api/v1/deployments/{response.Id}", response);
    }
}
```

## 3. TypeScript / React conventions

| Topic | Rule |
| --- | --- |
| Strictness | `strict: true`, `noUncheckedIndexedAccess`, ESLint (`typescript-eslint`, `react-hooks`, `jsx-a11y`), Prettier |
| Components | Function components; props typed with `interface`; one component per file; `PascalCase.tsx` |
| Hooks | `useXxx` in `features/<f>/hooks.ts`; TanStack Query hooks wrap `api.ts` calls |
| API types | Generated from OpenAPI only; no manual duplicates |
| Styling | Tailwind utilities; shared variants via `cva`-style helpers in `components/ui` |
| State | Server state → TanStack Query; UI state → `useState`/`useReducer`; no global store unless needed |
| Files | `kebab-case` for non-component files; feature folders |
| Tests | `*.test.tsx` next to the file |

## 4. Git workflow

| Topic | Rule |
| --- | --- |
| Branching | Trunk-based: `main` always deployable; short-lived `feat/…`, `fix/…`, `docs/…`, `chore/…` |
| Commits | **Conventional Commits** (`feat(policies): add DeploymentWindow rule`); scopes = module names |
| PRs | Even solo: open a PR per feature for CI + self-review; squash merge; template with checklist (tests, docs updated, migration reviewed, audit events added) |
| Tags | `vMAJOR.MINOR.PATCH`; `v0.x` until Phase 8 completes |
| Protection | `main` requires CI green |
| Docs | A PR that changes behaviour updates the relevant `docs/*.md`; a PR that makes an architectural decision adds an ADR |

## 5. Definition of Done (per feature)

- [ ] Code follows conventions; `dotnet format`/eslint clean
- [ ] Unit tests for domain logic; integration test for the endpoint/flow
- [ ] Audit events emitted for state changes
- [ ] Authorization requirement declared and tested
- [ ] OpenAPI annotations present; `gen:api` regenerated
- [ ] Metrics/logs added where meaningful
- [ ] Docs updated (`docs/*.md`, ADR if applicable, CHANGELOG entry)
- [ ] Demo seed updated if the feature needs data to be shown

## 6. Naming cheat-sheet

| Thing | Convention | Example |
| --- | --- | --- |
| Table | `schema.snake_plural` | `deploy.deployment_events` |
| Column | `snake_case` | `requested_at` |
| Enum in DB | `text` with PascalCase values | `'AwaitingApproval'` |
| REST path | `kebab-case`, plural nouns | `/api/v1/deployments/{id}/ai-summary` |
| JSON field | `camelCase` | `approvalsRequired` |
| Permission | `resource:verb` | `deployments:approve` |
| Audit action | `resource.verb_past` | `deployment.approved` |
| Metric | `aegisops_<noun>_<unit or total>` | `aegisops_ai_requests_total` |
| SignalR message | `PascalCase` event name | `DeploymentStatusChanged` |
| Redis key | `area:kind:id` | `ai:rl:m:{userId}`, `lock:deployment:{id}`, `idem:{principal}:{key}` |
| Job type | `VerbNoun` | `EvaluateDeployment` |
| Docker container (targets) | `<project>-<environment>` | `circuit-service-staging` |
| Docker labels | `aegisops.*` | `aegisops.deployment=<id>` |
