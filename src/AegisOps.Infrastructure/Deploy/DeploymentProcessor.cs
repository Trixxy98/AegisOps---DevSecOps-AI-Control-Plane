using System.Text.Json;
using AegisOps.Application.PolicyEngine;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Infrastructure.Deploy;

public static class DeploymentProcessor {
    public static async Task<bool> TryProcessOneAsync(
        AegisOpsDbContext db,
        TimeProvider time,
        string workerId,
        CancellationToken cancellationToken
    ) {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = time.GetUtcNow();
        var job = await db.Jobs
            .FromSqlInterpolated($"""
                SELECT * FROM jobs.jobs
                WHERE status = 'Pending' AND scheduled_at <= {now}
                ORDER BY scheduled_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .AsTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (job is null) {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        job.MarkRunning(now, workerId);
        await db.SaveChangesAsync(cancellationToken);

        try {
            var deploymentId = JsonDocument.Parse(job.Payload).RootElement.GetProperty("deploymentId").GetGuid();
            if (job.Type == JobType.EvaluateDeployment) {
                await EvaluateAsync(db, deploymentId, time, cancellationToken);
            } else if (job.Type == JobType.ExecuteDeployment) {
                await ExecuteAsync(db, deploymentId, time, cancellationToken);
            }

            job.Succeed(time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        } catch (Exception exception) {
            job.Fail(exception.Message, time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
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
        CancellationToken cancellationToken
    ) {
        var deployment = await db.Deployments.SingleAsync(item => item.Id == deploymentId, cancellationToken);
        var outcome = await DecideAsync(db, deployment, time, cancellationToken);
        if (outcome.Decision == PolicyDecision.Deny) {
            deployment.ApplyDecision(outcome, time.GetUtcNow());
            await AddEventAsync(db, deployment.Id, DeploymentEventType.Denied, outcome.Message ?? "Denied before execution.", time, cancellationToken);
            return;
        }

        var now = time.GetUtcNow();
        deployment.Start(now);
        await AddEventAsync(db, deployment.Id, DeploymentEventType.DeployStarted, "Noop executor started.", time, cancellationToken);
        var environment = await db.Environments.SingleAsync(item => item.Id == deployment.EnvironmentId, cancellationToken);
        environment.RecordDeployment(deployment.ArtifactId, deployment.Id);
        deployment.Succeed(time.GetUtcNow());
        await AddEventAsync(db, deployment.Id, DeploymentEventType.Succeeded, "Simulated deployment succeeded.", time, cancellationToken);
        db.AuditEvents.Add(AuditEvent.Record(
            time.GetUtcNow(),
            "System",
            null,
            "noop-executor",
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
            time.GetUtcNow()
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
