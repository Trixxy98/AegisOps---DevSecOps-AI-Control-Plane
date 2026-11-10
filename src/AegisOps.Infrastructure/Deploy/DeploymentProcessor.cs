using System.Diagnostics;
using System.Text.Json;
using AegisOps.Application.Observability;
using AegisOps.Application.Audit;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Docker;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Observability;
using AegisOps.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace AegisOps.Infrastructure.Deploy;

/// <summary>Runs a Docker deployment, reporting each timeline step through <paramref name="record"/> as it happens.</summary>
public delegate Task<DockerExecution> DeploymentStepExecutor(
    DeploymentTarget target,
    Artifact artifact,
    string projectSlug,
    string environmentName,
    Guid deploymentId,
    Func<DeploymentEventType, string, CancellationToken, Task> record,
    CancellationToken cancellationToken);

public static class DeploymentProcessor {
    public static async Task<bool> TryProcessOneAsync(
        AegisOpsDbContext db,
        IAuditWriter audit,
        IServiceScopeFactory scopes,
        TimeProvider time,
        string workerId,
        CancellationToken cancellationToken,
        IConnectionMultiplexer? redis = null
    ) {
        var job = await TryClaimOneAsync(db, time, workerId, cancellationToken);
        if (job is null) return false;
        using var activity = JobTraceContext.StartJob(job);

        var started = Stopwatch.GetTimestamp();
        var token = job.LeaseToken!.Value;
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = RenewAsync(scopes, job.Id, token, time, lost);
        Guid? executedDeploymentId = null;
        long? webhookStarted = null;
        var webhookDelivered = false;
        try {
            if (job.Type == JobType.SendNotification) {
                var eventId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("eventId").GetGuid();
                await using var configScope = scopes.CreateAsyncScope();
                webhookStarted = Stopwatch.GetTimestamp();
                webhookDelivered = await TeamWebhookSender.SendAsync(db, configScope.ServiceProvider.GetRequiredService<IConfiguration>(), eventId, lost.Token);
            } else if (job.Type == JobType.ParseScanReport) {
                var scanId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("scanId").GetGuid();
                await ScanReportStore.ParseQueuedAsync(db, scanId, time.GetUtcNow(), lost.Token);
            } else {
                var deploymentId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("deploymentId").GetGuid();
                if (job.Type == JobType.EvaluateDeployment) {
                    await EvaluateAsync(db, audit, deploymentId, time, lost.Token);
                } else if (job.Type == JobType.ExecuteDeployment) {
                    await ExecuteAsync(db, audit, deploymentId, time, lost.Token, redis, LeaseGuard(db, job.Id, token, time, lost));
                    executedDeploymentId = deploymentId;
                }
            }

            var pending = db.ChangeTracker.Entries<DeploymentEvent>()
                .Where(entry => entry.State == EntityState.Added).ToArray();
            var webhookDuration = webhookStarted is { } sendStarted ? Stopwatch.GetElapsedTime(sendStarted).TotalSeconds : (double?)null;
            // A skipped notification (no destination or not subscribed) is not a delivery attempt.
            var webhook = await FinishAsync(db, job, token, time, null, lost.Token, webhookAttempted: webhookDelivered);
            activity?.SetTag("job.status", job.Status.ToString());
            JobMetrics.Record(job.Type, job.Status, Stopwatch.GetElapsedTime(started).TotalSeconds);
            if (webhook is { } delivered) WebhookMetrics.Record(delivered, webhookDuration);
            foreach (var id in pending.Where(entry => entry.State == EntityState.Unchanged)
                         .Select(entry => entry.Entity.DeploymentId).Distinct())
                await DeploymentUpdates.PublishAsync(redis, id);
            // Refresh the Worker's runtime snapshot right away so the API reflects the new container.
            if (executedDeploymentId is { } executed)
                await EnvironmentRuntimeMonitor.RefreshAfterDeploymentAsync(scopes, executed, time, cancellationToken);
            return true;
        } catch (OperationCanceledException) when (lost.IsCancellationRequested) {
            activity?.SetTag("job.status", "LeaseLostOrCancelled");
            activity?.SetStatus(ActivityStatusCode.Error);
            return true;
        } catch (Exception exception) {
            // HTTP/DNS exceptions may contain the URL (including query tokens). Never persist
            // them in jobs.last_error or expose them through the admin jobs API.
            var webhookDuration = webhookStarted is { } sendStarted ? Stopwatch.GetElapsedTime(sendStarted).TotalSeconds : (double?)null;
            var webhook = await FinishAsync(db, job, token, time, job.Type == JobType.SendNotification ? "Webhook delivery failed." : exception.Message, lost.Token,
                webhookAttempted: job.Type == JobType.SendNotification);
            activity?.SetTag("job.status", job.Status.ToString());
            // Never attach the exception: messages can contain URLs or Docker details.
            activity?.SetStatus(ActivityStatusCode.Error);
            JobMetrics.Record(job.Type, job.Status, Stopwatch.GetElapsedTime(started).TotalSeconds);
            if (webhook is { } failed) WebhookMetrics.Record(failed, webhookDuration);
            return true;
        } finally {
            lost.Cancel();
            await heartbeat;
        }
    }

    public static async Task<Job?> TryClaimOneAsync(AegisOpsDbContext db, TimeProvider time, string workerId, CancellationToken cancellationToken) {
        Job? job = null;
        var staleTransitions = new List<(JobType Type, JobStatus Status)>();
        var staleWebhooks = new List<WebhookDeliveryOutcome>();
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
                    if (item.Type == JobType.SendNotification) {
                        var attempt = await db.WebhookDeliveryAttempts.SingleOrDefaultAsync(record => record.JobId == item.Id && record.AttemptNumber == item.Attempts, cancellationToken);
                        attempt?.Fail(now);
                        var outcome = item.Status == JobStatus.DeadLettered ? WebhookDeliveryOutcome.DeadLettered : WebhookDeliveryOutcome.Failed;
                        if (TeamWebhookDeliveryStatsStore.TryReadEventId(item.Payload) is { } staleEventId &&
                            await TeamWebhookDeliveryStatsStore.RecordAsync(db, staleEventId, outcome, now, cancellationToken))
                            staleWebhooks.Add(outcome);
                    }
                }
            }

            if (stale.Count > 0) {
                await db.SaveChangesAsync(cancellationToken);
                staleTransitions.AddRange(stale.Select(item => (item.Type, item.Status)));
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

            if (job is not null) {
                job.MarkRunning(now, workerId);
                if (job.Type == JobType.SendNotification)
                    db.WebhookDeliveryAttempts.Add(WebhookDeliveryAttempt.Start(job.Id, job.Attempts, now));
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        foreach (var (type, status) in staleTransitions) JobMetrics.RecordLeaseExpired(type, status);
        foreach (var outcome in staleWebhooks) WebhookMetrics.Record(outcome, null);
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

    /// <summary>
    /// Fences an intermediate commit to the worker that still owns the job lease. The guarded UPDATE renews
    /// <c>locked_at</c> inside the caller's transaction, so on PostgreSQL the row stays locked until the step
    /// commits and a concurrent stale-lease recovery cannot interleave. A lost lease cancels <paramref name="lost"/>,
    /// which is the same signal the heartbeat uses.
    /// </summary>
    internal static Func<CancellationToken, Task<bool>> LeaseGuard(AegisOpsDbContext db, Guid id, Guid token, TimeProvider time,
        CancellationTokenSource lost) => async ct => {
            if (!lost.IsCancellationRequested && await RenewLeaseAsync(db, id, token, time.GetUtcNow(), ct)) return true;
            lost.Cancel();
            return false;
        };

    public static async Task<bool> RenewLeaseAsync(AegisOpsDbContext db, Guid id, Guid token, DateTimeOffset now, CancellationToken ct) =>
        await db.Jobs.Where(j => j.Id == id && j.Status == JobStatus.Running && j.LeaseToken == token &&
                                 j.LockedAt >= now.AddMinutes(-2))
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.LockedAt, now), ct) == 1;

    /// <summary>
    /// Commits the job result. For a SendNotification job it also completes the attempt row and, when
    /// <paramref name="webhookAttempted"/>, increments the team's durable counters in the same transaction.
    /// Returns the webhook outcome to emit as telemetry after commit, or null when nothing was counted.
    /// </summary>
    private static async Task<WebhookDeliveryOutcome?> FinishAsync(AegisOpsDbContext db, Job job, Guid token, TimeProvider time, string? error,
        CancellationToken ct, bool webhookAttempted = false) {
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
        // After the failure branch above, which detaches every other tracked change: completing the
        // attempt earlier would discard its Failed status.
        var webhook = job.Type == JobType.SendNotification
            ? await CompleteWebhookAttemptAsync(db, job, current.Attempts, error is null, webhookAttempted, time.GetUtcNow(), ct)
            : null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return webhook;
    }

    /// <summary>
    /// Completes the attempt row of a SendNotification job whose status was already updated, and when the
    /// attempt really contacted (or tried to contact) a receiver, increments the team's durable counters.
    /// Runs inside the caller's transaction; the caller saves and commits.
    /// </summary>
    internal static async Task<WebhookDeliveryOutcome?> CompleteWebhookAttemptAsync(
        AegisOpsDbContext db, Job job, int attemptNumber, bool succeeded, bool attempted, DateTimeOffset now, CancellationToken ct
    ) {
        var attempt = await db.WebhookDeliveryAttempts.SingleAsync(item => item.JobId == job.Id && item.AttemptNumber == attemptNumber, ct);
        if (succeeded) attempt.Succeed(now); else attempt.Fail(now);
        if (!attempted || TeamWebhookDeliveryStatsStore.TryReadEventId(job.Payload) is not { } eventId) return null;
        var outcome = succeeded ? WebhookDeliveryOutcome.Succeeded
            : job.Status == JobStatus.DeadLettered ? WebhookDeliveryOutcome.DeadLettered : WebhookDeliveryOutcome.Failed;
        return await TeamWebhookDeliveryStatsStore.RecordAsync(db, eventId, outcome, now, ct) ? outcome : null;
    }

    public static async Task EvaluateAsync(
        AegisOpsDbContext db,
        IAuditWriter audit,
        Guid deploymentId,
        TimeProvider time,
        CancellationToken cancellationToken
    ) {
        using var activity = AegisOpsTracing.Source.StartActivity("EvaluateDeployment");
        var deployment = await db.Deployments.SingleAsync(item => item.Id == deploymentId, cancellationToken);
        if (deployment.Status is DeploymentStatus.Requested) {
            deployment.BeginEvaluation();
            await AddEventAsync(db, deployment.Id, DeploymentEventType.PolicyEvaluated, "Evaluating policies.", time, cancellationToken);
        }

        var outcome = await DecideAsync(db, deployment, time, cancellationToken);
        activity?.SetTag("decision", outcome.Decision.ToString());
        DomainMetrics.RecordPolicyEvaluation(outcome.Decision);
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
                JobTraceContext.DeploymentPayload(deployment.Id, deployment.CorrelationId),
                time.GetUtcNow(),
                deployment.CorrelationId
            ));
        }

        audit.Write(AuditEvent.Record(
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
        IAuditWriter audit,
        Guid deploymentId,
        TimeProvider time,
        CancellationToken cancellationToken,
        IConnectionMultiplexer? redis = null,
        Func<CancellationToken, Task<bool>>? leaseGuard = null,
        DeploymentStepExecutor? executor = null
    ) {
        using var activity = AegisOpsTracing.Source.StartActivity("ExecuteDeployment");
        var deployment = await db.Deployments.SingleAsync(item => item.Id == deploymentId, cancellationToken);
        var outcome = await DecideAsync(db, deployment, time, cancellationToken);
        if (outcome.Decision != PolicyDecision.Allow) {
            deployment.ApplyDecision(outcome, time.GetUtcNow());
            var denied = outcome.Decision == PolicyDecision.Deny;
            await AddEventAsync(db, deployment.Id,
                denied ? DeploymentEventType.Denied : DeploymentEventType.ApprovalRequested,
                outcome.Message ?? (denied ? "Denied before execution." : "Approval required before execution."),
                time, cancellationToken);
            audit.Write(AuditEvent.Record(time.GetUtcNow(), "System", null, "policy-engine", "deployment.evaluated",
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
            // DeployStarted also commits the Deploying status and the execution evaluation (non-terminal: no
            // deployment metrics). From here on a retried job cannot Start() again, so these steps belong to
            // exactly one execution attempt.
            await CommitStepAsync(db, deployment.Id, DeploymentEventType.DeployStarted,
                $"Docker executor started for {environment.Target.ContainerName}.", time, leaseGuard, redis, cancellationToken);
            var execution = await (executor ?? DockerDeploymentExecutor.ExecuteAsync)(
                environment.Target,
                artifact,
                project.Slug,
                environment.Name,
                deployment.Id,
                // Each Docker step is committed and hinted as it happens so viewers see it live.
                (type, message, token) => CommitStepAsync(db, deployment.Id, type, message, time, leaseGuard, redis, token),
                cancellationToken);
            if (!execution.Succeeded) {
                deployment.Fail(execution.Message, time.GetUtcNow());
                await AddEventAsync(db, deployment.Id, DeploymentEventType.Failed, execution.Message, time, cancellationToken);
                audit.Write(AuditEvent.Record(time.GetUtcNow(), "System", null, "docker-executor", "deployment.failed", "Deployment", deployment.Id, "Failed", deployment.CorrelationId));
                return;
            }
        } else {
            await AddEventAsync(db, deployment.Id, DeploymentEventType.DeployStarted, "Noop executor started.", time, cancellationToken);
        }

        var docker = string.Equals(environment.Target.Type, "docker", StringComparison.OrdinalIgnoreCase);
        environment.RecordDeployment(deployment.ArtifactId, deployment.Id);
        deployment.Succeed(time.GetUtcNow());
        await AddEventAsync(db, deployment.Id, DeploymentEventType.Succeeded, docker ? "Container is healthy." : "Simulated deployment succeeded.", time, cancellationToken);
        audit.Write(AuditEvent.Record(
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

    /// <summary>
    /// Commits one intermediate timeline step in its own short transaction and then publishes the Redis hint.
    /// The lease is checked first inside the same transaction; a lost lease throws before anything is written.
    /// Only the event (and the notification job SaveChanges enqueues for it) is pending at Docker step time:
    /// terminal deployment transitions stay tracked for FinishAsync, so DomainMetrics never sees them here.
    /// </summary>
    internal static async Task CommitStepAsync(
        AegisOpsDbContext db,
        Guid deploymentId,
        DeploymentEventType type,
        string message,
        TimeProvider time,
        Func<CancellationToken, Task<bool>>? leaseGuard,
        IConnectionMultiplexer? redis,
        CancellationToken cancellationToken
    ) {
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken)) {
            if (leaseGuard is not null && !await leaseGuard(cancellationToken))
                throw new OperationCanceledException("Job lease was lost.");
            await AddEventAsync(db, deploymentId, type, message, time, cancellationToken);
            try {
                await db.SaveChangesAsync(cancellationToken);
            } catch {
                // Never leave a failed step pending: FinishAsync would retry the same insert.
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added &&
                             e.Entity is DeploymentEvent or Job).ToList())
                    entry.State = EntityState.Detached;
                throw;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        await DeploymentUpdates.PublishAsync(redis, deploymentId);
    }

    private static async Task AddEventAsync(
        AegisOpsDbContext db,
        Guid deploymentId,
        DeploymentEventType type,
        string message,
        TimeProvider time,
        CancellationToken cancellationToken
    ) {
        // Committed events are already counted by the store query; only still-Added local rows are pending.
        // (Counting every Local row double-counted events committed earlier in the same job.)
        var stored = await db.DeploymentEvents.Where(item => item.DeploymentId == deploymentId)
            .MaxAsync(item => (int?)item.Sequence, cancellationToken) ?? 0;
        var pending = db.ChangeTracker.Entries<DeploymentEvent>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.DeploymentId == deploymentId)
            .Select(entry => entry.Entity.Sequence).DefaultIfEmpty(0).Max();
        var sequence = Math.Max(stored, pending) + 1;
        db.DeploymentEvents.Add(DeploymentEvent.Create(deploymentId, sequence, time.GetUtcNow(), type, message));
    }
}
