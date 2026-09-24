# 16 — Testing Strategy

Tests exist to prove **decisions** are correct, not to inflate coverage. The policy engine
and the deployment state machine get the deepest coverage; CRUD gets smoke coverage.

## 1. Test pyramid

| Level | Project | Tools | Speed | What |
| --- | --- | --- | --- | --- |
| Unit (Domain) | `AegisOps.Domain.Tests` | xUnit, Shouldly | ms | Policy rules, `PolicyEngine`, `Deployment` state machine, `ApiKey` hashing, sensitive-data detectors, severity mapping |
| Unit (Application) | `AegisOps.Application.Tests` | xUnit, NSubstitute | ms | Use-case handlers with faked ports (e.g. `RequestDeploymentHandler` enqueues job + audit) |
| Architecture | `AegisOps.Architecture.Tests` | NetArchTest.Rules | ms | Dependency rule, module isolation, naming |
| Integration (API) | `AegisOps.Api.IntegrationTests` | `WebApplicationFactory`, Testcontainers (PostgreSQL, Redis), Respawn | s | Endpoints end-to-end through real DB: auth, authorization, idempotency, scan ingestion, deployment flow with `noop` executor + in-process worker loop |
| Integration (Infra) | `AegisOps.Infrastructure.Tests` | Testcontainers | s | Job queue claim semantics (`SKIP LOCKED`), SARIF parsers on real fixture files, Redis rate limiter Lua script |
| Frontend unit | `AegisOps.Web` | Vitest, Testing Library, MSW | ms | `PolicyEvaluationPanel`, `RuleBuilder`, auth interceptor refresh logic |
| E2E (V2) | `tests/e2e` | Playwright | min | The four journeys against the Compose stack |

Targets: Domain ≥ 90% line / ≥ 95% branch on `Policies` namespace; overall backend ≥ 75%.
Coverage measured with coverlet, reported in CI job summary.

## 2. Conventions

- Test names: `Method_Scenario_ExpectedResult` or sentence style with `[Fact(DisplayName)]`.
- Builders/fixtures: `PolicyContextBuilder` test helper with fluent defaults
  (`.WithTier(Production).WithTrivy(critical: 1)…`), `DeploymentFactory`, `ArtifactFactory`.
- Time: always `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`).
- No test touches real Ollama or Docker; `ILlmClient`/`IDeploymentExecutor` fakes.
- Integration tests share one container per test class (`IClassFixture`), reset DB with
  Respawn between tests.

## 3. Policy engine test matrix

All cases run against the seeded policies from [05 §7](05-policy-engine.md#7-worked-examples).
Context defaults unless stated: tests passed, all three scans present and fresh, 0 findings,
branch `main`, digest present, staging succeeded 2 h ago, Tue 14:00 MYT, 0 approvals.

| # | Tier | Variation | Expected decision | Failing rule(s) |
| --- | --- | --- | --- | --- |
| 1 | Development | none | `Allow` | — |
| 2 | Development | tests failed | `Deny` | `RequireTestsPassed` |
| 3 | Staging | none | `Allow` | — |
| 4 | Staging | Semgrep scan missing | `Deny` | `RequireScan` |
| 5 | Staging | Trivy High = 12 | `Allow` + warning | `MaxFindings{High,10}` (Warn) |
| 6 | Staging | Trivy Critical = 1 | `Deny` | `MaxFindings{Critical,0}` |
| 7 | Staging | Critical finding **suppressed** | `Allow` | — (suppressed excluded) |
| 8 | Production | none | `RequireApproval(2)` | `RequireApprovals` quorum |
| 9 | Production | 2 approvals (Approver + Security) | `Allow` | — |
| 10 | Production | 2 approvals but one from requester | `RequireApproval(2)` | requester excluded |
| 11 | Production | 2 approvals, one from Developer role | `RequireApproval(2)` | role not allowed |
| 12 | Production | 1 approval | `RequireApproval(2)` | quorum |
| 13 | Production | Trivy High = 4, 2 approvals | `Allow` | `MaxFindings{High,3}` fails with RequireApproval effect → satisfied by quorum |
| 14 | Production | Trivy Critical = 1, 2 approvals | `Deny` | `MaxFindings{Critical,0}` — approvals cannot override Deny |
| 15 | Production | branch `feature/x` | `Deny` | `AllowedBranches` |
| 16 | Production | branch `refs/tags/v1.8.2` | `RequireApproval(2)` | — |
| 17 | Production | no image digest | `Deny` | `RequireImageDigest` |
| 18 | Production | staging never succeeded | `Deny` | `RequirePriorEnvironment` |
| 19 | Production | staging succeeded 40 days ago (maxAge 30d) | `Deny` | `RequirePriorEnvironment` age |
| 20 | Production | Fri 17:30 MYT | `Deny` | `DeploymentWindow` |
| 21 | Production | Fri 14:59 MYT | `RequireApproval(2)` | — |
| 22 | Production | Sat 10:00 MYT | `Deny` | `DeploymentWindow` |
| 23 | Production | Mon 08:59 vs 09:00 MYT (boundary) | `Deny` / `RequireApproval(2)` | `DeploymentWindow` inclusive start |
| 24 | Production | evaluation at 01:00 **UTC** = 09:00 MYT | `RequireApproval(2)` | timezone conversion |
| 25 | Production | Team policy `MaxFindings{Semgrep,Medium,0}` + Semgrep Medium = 1 | `RequireApproval(2)` (already required) with rule listed as failed | union of policies |
| 26 | Production | Global baseline **disabled** | `RequireApproval(2)` and evaluated-policies lists it as skipped | transparency |
| 27 | Production | scan older than `maxAgeHours` | `Deny` | `RequireScan` age |
| 28 | any | `MaxFindings{includeUnknown:false}` with Unknown = 5 | pass | Unknown excluded |
| 29 | any | `MaxFindings{includeUnknown:true}` with Unknown = 5, max 0 | fail | Unknown included |
| 30 | any | rule with invalid parameters | rejected at validation (not evaluation) | validator |
| 31 | any | no applicable policies at all | `Allow` with empty rule list | fail-open only when *no* policy configured — documented and logged as warning |
| 32 | any | scan `Status = Failed` present | treated as missing → `RequireScan` fails | fail closed |

Property-style tests (FsCheck optional): decision is monotonic — adding a failing `Deny`
rule never makes the decision less restrictive; adding approvals never makes it more
restrictive.

## 4. Deployment state machine tests

- Every legal transition from the diagram in [04 §4.1](04-domain-model.md#41-deployment)
  succeeds and appends exactly one `DeploymentEvent`.
- Every illegal transition throws `InvalidDeploymentTransitionException` (generated
  combinatorially from `Enum.GetValues`).
- `AddApproval`: rejects duplicate approver, requester, wrong status; quorum triggers
  `Approved` and `DeploymentApproved` event exactly once.
- `Approved → Deploying` re-check of `DeploymentWindow` denies when window closed.

## 5. Architecture tests

```csharp
Types.InAssembly(DomainAssembly).ShouldNot().HaveDependencyOnAny("AegisOps.Application", "AegisOps.Infrastructure", "Microsoft.EntityFrameworkCore")
Types.InAssembly(ApplicationAssembly).ShouldNot().HaveDependencyOn("AegisOps.Infrastructure")
Types.InNamespace("AegisOps.Domain.Deployments").ShouldNot().HaveDependencyOn("AegisOps.Domain.Ai")   // module isolation (allowed pairs listed explicitly)
Types.That().ImplementInterface(typeof(IJobHandler<>)).Should().ResideInNamespace("AegisOps.Application")
Types.That().Inherit(typeof(BackgroundService)).Should().ResideInNamespaceStartingWith("AegisOps.Worker").Or().ResideInNamespaceStartingWith("AegisOps.Api.Realtime")
```

## 6. Integration test scenarios (API)

| Scenario | Asserts |
| --- | --- |
| Login → refresh → reuse old refresh token | 200, 200, then 401 + family revoked + audit `auth.refresh_reuse_detected` |
| Developer requests deployment for a project in another team | 404 (no leak) + audit `Denied` |
| API key with `scans:write` only calls `POST /deployments` | 403 |
| `POST /artifacts` twice with same `Idempotency-Key` | same body, one row |
| Upload Trivy SARIF fixture | scan `Completed`, summary counts match fixture, findings paginated |
| Full flow: artifact → scans → request Staging → worker loop → `Succeeded` (noop executor) | timeline has expected event sequence; audit has `deployment.requested/evaluated/succeeded` |
| Production flow with approvals | `AwaitingApproval` → approve ×2 → `Approved` → `Succeeded`; requester approval rejected with 403 |
| Denied flow | Critical finding → `Denied`; `POST /approvals` returns 409 invalid-transition |
| Policy edit increments version and next evaluation records new version | |
| AI chat with fake AWS key under `Redact` policy | `AiRequest.DetectedSensitiveTypes = [AWS_ACCESS_KEY]`, redactions = 1, fake `ILlmClient` received redacted prompt |
| AI chat exceeding rate limit | 429 + `Retry-After` + `AiRequest.Status = Blocked` |
| Audit table has no `UPDATE`/`DELETE` privilege for app role | executing `DELETE` via app connection fails |

## 7. Infrastructure tests

- **Job queue**: two concurrent claimers never receive the same job; failed job is
  rescheduled with backoff; `max_attempts` → `DeadLettered`; reaper resets stale `Running`.
- **SARIF parsers**: fixtures under `tests/fixtures/sarif/` for each scanner (clean report,
  report with findings, malformed report); severity mapping table-driven.
- **Redis rate limiter**: sliding window correctness with `FakeTimeProvider`-driven scores;
  atomicity under parallel calls.

## 8. Frontend tests

- `PolicyEvaluationPanel` renders Deny/RequireApproval/Allow states from fixtures.
- `RuleBuilder` builds valid payloads for each rule type; invalid parameters show errors.
- Axios interceptor: single refresh in flight during concurrent 401s; logout on refresh
  failure.
- `useRealtimeInvalidation`: each hub message invalidates the mapped keys.

## 9. Running tests

```bash
dotnet test                                   # all backend (integration tests need Docker)
dotnet test --filter Category!=Integration    # fast loop
npm --prefix src/AegisOps.Web test            # frontend
```

CI runs everything on every PR; integration tests use Testcontainers on `ubuntu-latest`.
