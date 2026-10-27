using System.Text.Json;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Docker;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace AegisOps.Infrastructure.Deploy;

public static class DeploymentProcessor {
    public static async Task<bool> TryProcessOneAsync(
        AegisOpsDbContext db,
        IServiceScopeFactory scopes,
        TimeProvider time,
        string workerId,
        CancellationToken cancellationToken,
        IConnectionMultiplexer? redis = null
    ) {
        var job = await TryClaimOneAsync(db, time, workerId, cancellationToken);
        if (job is null) return false;

        var token = job.LeaseToken!.Value;
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RenewAsync(scopes, job.Id, token, time, lost);
        try {
            if (job.Type == JobType.ParseScanReport) {
                var scanId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("scanId").GetGuid();
                await ScanReportStore.ParseQueuedAsync(db, scanId, time.GetUtcNow(), lost.Token);
            } else {
                var deploymentId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("deploymentId").GetGuid();
                if (job.Type == JobType.EvaluateDeployment) {
                    await EvaluateAsync(db, deploymentId, time, lost.Token);
                } else if (job.Type == JobType.ExecuteDeployment) {
                    await ExecuteAsync(db, deploymentId, time, lost.Token, redis);
                }
            }

            var pending = db.ChangeTracker.Entries<DeploymentEvent>()
                .Where(entry => entry.State == EntityState.Added).ToArray();
            await FinishAsync(db, job, token, time, null, lost.Token);
            foreach (var id in pending.Where(entry => entry.State == EntityState.Unchanged)
                         .Select(entry => entry.Entity.DeploymentId).Distinct())
                await DeploymentUpdates.PublishAsync(redis, id);
            return true;
        } catch (OperationCanceledException) when (lost.IsCancellationRequested) {
            return true;
        } catch (Exception exception) {
            await FinishAsync(db, job, token, time, exception.Message, lost.Token);
            return true;
        } finally {
            lost.Cancel();
            await heartbeat;
        }
    }

    public static async Task<Job?> TryClaimOneAsync(AegisOpsDbContext db, TimeProvider time, string workerId, CancellationToken cancellationToken) {
        Job? job = null;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
            // Serialize the short claim decision across all API and worker instances. Never hold
            // this transaction while performing external work.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(643719, 1)", cancellationToken);
            var now = time.GetUtcNow();
            var staleBefore = now.AddMinutes(-2);
            var stale = await db.Jobs.FromSqlInterpolated($"""
                SELECT * FROM jobs.jobs WHERE status = 'Running'
                  AND (locked_at IS NULL OR locked_at < {staleBefore})
                FOR UPDATE
                """)
                .AsTracking()
                .ToListAsync(cancellationToken);
            foreach (var item in stale) {
                if (item.Type == JobType.ExecuteDeployment) {
                    // Docker calls cannot be fenced by PostgreSQL. Never automatically replay an
                    // interrupted execution whose external side effects may already have happened.
                    item.Abandon("Execution lease expired; inspect the Docker target before retrying.", now);
                } else {
                    item.Fail("Worker lease expired.", now);
                }
            }

            if (stale.Count > 0) {
                await db.SaveChangesAsync(cancellationToken);
            }

            var candidates = await db.Jobs
                .FromSqlInterpolated($"""
                    SELECT * FROM jobs.jobs
                    WHERE status = 'Pending' AND scheduled_at <= {now}
                    ORDER BY scheduled_at
                    FOR UPDATE SKIP LOCKED
                    """)
                .AsTracking()
                .ToListAsync(cancellationToken);

            foreach (var candidate in candidates) {
                if (candidate.Type != JobType.ExecuteDeployment) {
                    job = candidate;
                    break;
                }

                var deploymentId = JsonDocument.Parse(candidate.Payload).RootElement.GetProperty("deploymentId").GetGuid();
                var deployment = await db.Deployments.AsNoTracking().SingleAsync(item => item.Id == deploymentId, cancellationToken);
                var environment = await db.Environments.AsNoTracking().SingleAsync(item => item.Id == deployment.EnvironmentId, cancellationToken);
                var name = environment.Target.Type == "docker" ? environment.Target.ContainerName : null;
                var port = environment.Target.Type == "docker" ? environment.Target.HostPort : 0;
                // The same environment OR Docker container name OR bound host port must be idle.
                // The claim transaction serializes this predicate, including empty target sets.
                var busy = await db.Database.SqlQueryRaw<int>("""
                    SELECT 1 AS "Value" FROM jobs.jobs j
                    JOIN deploy.deployments d ON d.id = (j.payload->>'deploymentId')::uuid
                    JOIN org.environments e ON e.id = d.environment_id
                    WHERE j.type = 'ExecuteDeployment' AND
                      (j.status = 'Running' OR (j.status = 'DeadLettered' AND
                       j.last_error LIKE 'Execution %; inspect the Docker target before retrying%'))
                      AND (d.environment_id = {0} OR
                           ({1} <> '' AND e.target->>'Type' = 'docker' AND
                            (e.target->>'ContainerName' = {1} OR (e.target->>'HostPort')::int = {2})))
                    LIMIT 1
                    """, deployment.EnvironmentId, name ?? string.Empty, port).AnyAsync(cancellationToken);
                if (!busy) {
                    job = candidate;
                    break;
                }
            }

            if (job is null) {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            job.MarkRunning(now, workerId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return job;
    }

    private static async Task RenewAsync(IServiceScopeFactory scopes, Guid id, Guid token, TimeProvider time, CancellationTokenSource lost) {
        try {
            while (!lost.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromSeconds(10), lost.Token);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
                if (!await RenewLeaseAsync(db, id, token, time.GetUtcNow(), lost.Token)) lost.Cancel();
            }
        } catch (OperationCanceledException) when (lost.IsCancellationRequested) {
        } catch (Exception) {
            // A failed heartbeat is not evidence that the lease still belongs to us.
            lost.Cancel();
        }
    }

    public static async Task<bool> RenewLeaseAsync(AegisOpsDbContext db, Guid id, Guid token, DateTimeOffset now, CancellationToken ct) =>
        await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.Running && j.LeaseToken == token &&
                                 j.LockedAt >= now.AddMinutes(-2))
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.LockedAt, now), ct) == 1;

    private static async Task FinishAsync(AegisOpsDbContext db, Job job, Guid token, TimeProvider time, string? error, CancellationToken ct) {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var owner = await db.Jobs.FromSqlInterpolated($"SELECT * FROM jobs.jobs WHERE id = {job.Id} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var current = owner.Single();
        if (current.Status != JobStatus.Running || current.LeaseToken != token || current.LockedAt < time.GetUtcNow().AddMinutes(-2)) {
            throw new OperationCanceledException("Job lease was lost.");
        }
        if (error is null && job.Type == JobType.EvaluateDeployment) {
            var id = JsonDocument.Parse(job.Payload).RootElement.GetProperty("deploymentId").GetGuid();
            var tracked = db.Deployments.Local.SingleOrDefault(d => d.Id == id);
            if (tracked is not null) {
                var persisted = (await db.Deployments.FromSqlInterpolated($"SELECT * FROM deploy.deployments WHERE id = {id} FOR UPDATE")
                    .AsNoTracking().ToListAsync(ct)).Single();
                // A cancellation (or another evaluator) may have won while policy evaluation ran.
                // Do not overwrite that decision or publish the losing evaluator's events.
                if (persisted.Status != db.Entry(tracked).Property(d => d.Status).OriginalValue) {
                    foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity != job && e.State != EntityState.Unchanged).ToList())
                        entry.State = EntityState.Detached;
                }
            }
        }
        if (error is null) job.Succeed(time.GetUtcNow());
        else {
            // A failed attempt must not commit partially tracked deployment changes.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity != job && e.State != EntityState.Unchanged).ToList())
                entry.State = EntityState.Detached;
            if (job.Type == JobType.ExecuteDeployment)
                job.Abandon($"Execution interrupted; inspect the Docker target before retrying: {error}", time.GetUtcNow());
            else job.Fail(error, time.GetUtcNow());
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public static async Task EvaluateAsync(
        AegisOpsDbContext db,
        Guid deploymentId,
        TimeProvider time,
        CancellationToken cancellationToken
    ) {
        var deployment = await db.Deployments.SingleAsync(item => item.Id == deploymentId, cancellationToken);
        if (deployment.Status is DeploymentStatus.Requested) {
            deployment.BeginEvaluation();
            await AddEventAsync(db, deployment.Id, DeploymentEventType.PolicyEvaluated, "Evaluating policies.", time, cancellationToken);
        }

        var outcome = await DecideAsync(db, deployment, time, cancellationToken);
        deployment.ApplyDecision(outcome, time.GetUtcNow());
        var message = outcome.Decision switch {
            PolicyDecision.Deny => outcome.Message ?? "Denied.",
            PolicyDecision.RequireApproval => outcome.Message ?? "Approval required.",
            _ => "Approved by policy.",
        };
        var eventType = outcome.Decision switch {
            PolicyDecision.Deny => DeploymentEventType.Denied,
            PolicyDecision.RequireApproval => DeploymentEventType.ApprovalRequested,
            _ => DeploymentEventType.Approved,
        };
        await AddEventAsync(db, deployment.Id, eventType, message, time, cancellationToken);

        if (outcome.Decision == PolicyDecision.Allow) {
            db.Jobs.Add(Job.Enqueue(
                JobType.ExecuteDeployment,
                JsonSerializer.Serialize(new { deploymentId = deployment.Id }),
                time.GetUtcNow(),
                deployment.CorrelationId
            ));
        }

        db.AuditEvents.Add(AuditEvent.Record(
            time.GetUtcNow(),
            "System",
            null,
            "policy-engine",
            "deployment.evaluated",
            "Deployment",
            deployment.Id,
            outcome.Decision.ToString(),
            deployment.CorrelationId
        ));
    }

    public static async Task ExecuteAsync(
        AegisOpsDbContext db,
        Guid deploymentId,
        TimeProvider time,
        CancellationToken cancellationToken,
        IConnectionMultiplexer? redis = null
    ) {
        var deployment = await db.Deployments.SingleAsync(item => item.Id == deploymentId, cancellationToken);
        var outcome = await DecideAsync(db, deployment, time, cancellationToken);
        if (outcome.Decision != PolicyDecision.Allow) {
            deployment.ApplyDecision(outcome, time.GetUtcNow());
            var denied = outcome.Decision == PolicyDecision.Deny;
            await AddEventAsync(db, deployment.Id,
                denied ? DeploymentEventType.Denied : DeploymentEventType.ApprovalRequested,
                outcome.Message ?? (denied ? "Denied before execution." : "Approval required before execution."),
                time, cancellationToken);
            db.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), "System", null, "policy-engine", "deployment.evaluated",
                "Deployment", deployment.Id, outcome.Decision.ToString(), deployment.CorrelationId));
            return;
        }

        deployment.RecordExecutionEvaluation(outcome.EvaluationId);
        var now = time.GetUtcNow();
        deployment.Start(now);
        var environment = await db.Environments.SingleAsync(item => item.Id == deployment.EnvironmentId, cancellationToken);
        if (string.Equals(environment.Target.Type, "docker", StringComparison.OrdinalIgnoreCase)) {
            var artifact = await db.Artifacts.SingleAsync(item => item.Id == deployment.ArtifactId, cancellationToken);
            var project = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == deployment.ProjectId, cancellationToken);
            await AddEventAsync(db, deployment.Id, DeploymentEventType.DeployStarted, $"Docker executor started for {environment.Target.ContainerName}.", time, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await DeploymentUpdates.PublishAsync(redis, deployment.Id);
            var execution = await DockerDeploymentExecutor.ExecuteAsync(
                environment.Target,
                artifact,
                project.Slug,
                environment.Name,
                deployment.Id,
                (type, message, token) => AddEventAsync(db, deployment.Id, type, message, time, token),
                cancellationToken);
            if (!execution.Succeeded) {
                deployment.Fail(execution.Message, time.GetUtcNow());
                await AddEventAsync(db, deployment.Id, DeploymentEventType.Failed, execution.Message, time, cancellationToken);
                db.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), "System", null, "docker-executor", "deployment.failed", "Deployment", deployment.Id, "Failed", deployment.CorrelationId));
                return;
            }
        } else {
            await AddEventAsync(db, deployment.Id, DeploymentEventType.DeployStarted, "Noop executor started.", time, cancellationToken);
        }

        var docker = string.Equals(environment.Target.Type, "docker", StringComparison.OrdinalIgnoreCase);
        environment.RecordDeployment(deployment.ArtifactId, deployment.Id);
        deployment.Succeed(time.GetUtcNow());
        await AddEventAsync(db, deployment.Id, DeploymentEventType.Succeeded, docker ? "Container is healthy." : "Simulated deployment succeeded.", time, cancellationToken);
        db.AuditEvents.Add(AuditEvent.Record(
            time.GetUtcNow(),
            "System",
            null,
            docker ? "docker-executor" : "noop-executor",
            "deployment.succeeded",
            "Deployment",
            deployment.Id,
            "Succeeded",
            deployment.CorrelationId
        ));
    }

    private static async Task<PolicyOutcome> DecideAsync(
        AegisOpsDbContext db,
        Deployment deployment,
        TimeProvider time,
        CancellationToken cancellationToken
    ) {
        var artifact = await db.Artifacts.AsNoTracking().SingleAsync(item => item.Id == deployment.ArtifactId, cancellationToken);
        var environment = await db.Environments.AsNoTracking().SingleAsync(item => item.Id == deployment.EnvironmentId, cancellationToken);
        var project = await db.Projects.AsNoTracking().SingleAsync(item => item.Id == deployment.ProjectId, cancellationToken);
        var priorEnvironment = await db.Environments.AsNoTracking()
            .Where(item => item.ProjectId == project.Id && item.Order < environment.Order)
            .OrderByDescending(item => item.Order)
            .FirstOrDefaultAsync(cancellationToken);
        PriorSuccess? prior = null;
        if (priorEnvironment is not null) {
            var previous = await db.Deployments.AsNoTracking()
                .Where(item => item.ArtifactId == artifact.Id && item.EnvironmentId == priorEnvironment.Id && item.Status == DeploymentStatus.Succeeded)
                .OrderByDescending(item => item.CompletedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (previous?.CompletedAt is not null) {
                prior = new PriorSuccess(priorEnvironment.Tier, previous.CompletedAt.Value);
            }
        }

        var approvedIds = await db.Approvals.AsNoTracking()
            .Where(item => item.DeploymentId == deployment.Id && item.Decision == ApprovalDecision.Approved)
            .Select(item => item.ApproverId)
            .ToListAsync(cancellationToken);
        var satisfied = 0;
        foreach (var approverId in approvedIds) {
            if (approverId == deployment.RequestedById) {
                continue;
            }

            satisfied++;
        }

        var policies = await db.Policies.AsNoTracking().Where(item => item.ArchivedAt == null).ToListAsync(cancellationToken);
        var rules = await db.PolicyRules.AsNoTracking().Where(item => item.IsEnabled).ToListAsync(cancellationToken);
        var applicable = policies.Select(policy => new ApplicablePolicy(
            policy.Id,
            policy.Name,
            policy.Version,
            policy.Scope,
            policy.ScopeId,
            policy.AppliesToTiers,
            policy.IsEnabled,
            rules.Where(rule => rule.PolicyId == policy.Id).Select(rule => new ApplicableRule(
                policy.Id,
                policy.Name,
                policy.Version,
                rule.Id,
                rule.Type,
                rule.Effect,
                rule.Parameters,
                rule.Order
            )).ToArray()
        )).ToArray();

        var scans = (await db.SecurityScans.AsNoTracking()
            .Where(item => item.ArtifactId == artifact.Id)
            .ToListAsync(cancellationToken))
            .Select(item => new ScanEvidence(
                item.Scanner.ToString(),
                item.Status.ToString(),
                item.CompletedAt,
                item.Summary.Critical,
                item.Summary.High,
                item.Summary.Medium,
                item.Summary.Low,
                item.Summary.Info,
                item.Summary.Unknown
            ))
            .ToArray();

        var result = PolicyEngine.Evaluate(new PolicyContext(
            artifact.Branch,
            artifact.BuildStatus,
            artifact.TestStatus,
            artifact.ImageDigest,
            environment.Tier,
            project.Id,
            project.TeamId,
            prior,
            satisfied,
            time.GetUtcNow(),
            scans
        ), applicable);

        var evaluation = PolicyEvaluation.Create(
            deployment.Id,
            result.Decision,
            result.ApprovalsRequired,
            time.GetUtcNow(),
            JsonSerializer.Serialize(result.Results)
        );
        db.PolicyEvaluations.Add(evaluation);
        return new PolicyOutcome(result.Decision, evaluation.Id, result.ApprovalsRequired, result.Message);
    }

    private static async Task AddEventAsync(
        AegisOpsDbContext db,
        Guid deploymentId,
        DeploymentEventType type,
        string message,
        TimeProvider time,
        CancellationToken cancellationToken
    ) {
        var stored = await db.DeploymentEvents.CountAsync(item => item.DeploymentId == deploymentId, cancellationToken);
        var pending = db.DeploymentEvents.Local.Count(item => item.DeploymentId == deploymentId);
        var sequence = stored + pending + 1;
        db.DeploymentEvents.Add(DeploymentEvent.Create(deploymentId, sequence, time.GetUtcNow(), type, message));
    }
}
