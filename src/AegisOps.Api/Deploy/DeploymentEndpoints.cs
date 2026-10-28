using System.Security.Claims;
using System.Text.Json;
using AegisOps.Api.Idempotency;
using AegisOps.Application.Idempotency;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Deploy;
using StackExchange.Redis;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Deploy;

public sealed record RequestDeploymentRequest(Guid ArtifactId, Guid? EnvironmentId, string? EnvironmentTier, string? Reason);

public sealed record DeploymentDecisionRequest(string Decision, string? Comment);

public static class DeploymentEndpoints {
    public static async Task<IResult> Request(
        RequestDeploymentRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        IIdempotencyStore idempotency,
        HttpContext http
    ) {
        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null) {
            return actor.Error;
        }

        var cancellationToken = http.RequestAborted;
        var artifact = await db.Artifacts.SingleOrDefaultAsync(item => item.Id == request.ArtifactId, cancellationToken);
        AegisOps.Domain.Organization.Environment? environment = null;
        if (request.EnvironmentId is Guid environmentId && environmentId != Guid.Empty) {
            environment = await db.Environments.SingleOrDefaultAsync(item => item.Id == environmentId, cancellationToken);
        } else if (artifact is not null && Enum.TryParse<EnvironmentTier>(request.EnvironmentTier, true, out var tier)) {
            environment = await db.Environments.SingleOrDefaultAsync(
                item => item.ProjectId == artifact.ProjectId && item.Tier == tier,
                cancellationToken);
        }
        if (artifact is null || environment is null || artifact.ProjectId != environment.ProjectId) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var denied = await AuthorizeTargetAsync(actor, users, db, artifact.ProjectId, environment, cancellationToken);
        if (denied is not null) {
            return denied;
        }

        var replay = await IdempotencyRequests.ReplayAsync(http, principal, idempotency,
            IdempotencyRequests.DeploymentIdentity("request", request));
        if (replay is not null) {
            return replay;
        }

        var now = time.GetUtcNow();
        var deployment = Enqueue(
            db,
            artifact,
            environment,
            now,
            actor,
            request.Reason,
            environment.CurrentArtifactId);
        await db.SaveChangesAsync(cancellationToken);
        await DeploymentUpdates.PublishAsync(http.RequestServices.GetService<IConnectionMultiplexer>(), deployment.Id);
        return await IdempotencyRequests.FinishAsync(http, idempotency, StatusCodes.Status202Accepted, new {
            deployment.Id,
            status = deployment.Status.ToString(),
        });
    }

    public static async Task<IResult> Rollback(
        Guid id,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        IIdempotencyStore idempotency,
        HttpContext http
    ) {
        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null) {
            return actor.Error;
        }

        var cancellationToken = http.RequestAborted;
        var source = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null) {
            return Results.Problem(title: "Deployment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var environment = await db.Environments.SingleOrDefaultAsync(item => item.Id == source.EnvironmentId, cancellationToken);
        if (environment is null || environment.ProjectId != source.ProjectId) {
            return Results.Problem(title: "Deployment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var denied = await AuthorizeTargetAsync(actor, users, db, source.ProjectId, environment, cancellationToken);
        if (denied is not null) {
            return denied;
        }

        if (source.Status != DeploymentStatus.Succeeded) {
            return Results.Problem(title: "Only a succeeded deployment can be rolled back.", statusCode: StatusCodes.Status409Conflict);
        }

        if (source.PreviousArtifactId is null) {
            return Results.Problem(title: "This deployment has no predecessor to restore.", statusCode: StatusCodes.Status409Conflict);
        }

        var artifact = await db.Artifacts.SingleOrDefaultAsync(item => item.Id == source.PreviousArtifactId, cancellationToken);
        if (artifact is null || environment is null || artifact.ProjectId != environment.ProjectId) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var replay = await IdempotencyRequests.ReplayAsync(http, principal, idempotency,
            IdempotencyRequests.DeploymentIdentity("rollback", new { id }));
        if (replay is not null) {
            return replay;
        }

        var now = time.GetUtcNow();
        var deployment = Enqueue(
            db,
            artifact,
            environment,
            now,
            actor,
            $"rollback of {source.Id:D}",
            environment.CurrentArtifactId);
        await db.SaveChangesAsync(cancellationToken);
        await DeploymentUpdates.PublishAsync(http.RequestServices.GetService<IConnectionMultiplexer>(), deployment.Id);
        return await IdempotencyRequests.FinishAsync(http, idempotency, StatusCodes.Status202Accepted, new {
            deployment.Id,
            status = deployment.Status.ToString(),
        });
    }

    public static async Task<IResult> List(
        AegisOpsDbContext db,
        ClaimsPrincipal principal,
        UserManager<User> users,
        HttpContext http,
        Guid? projectId = null,
        string? status = null
    ) {
        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        var query = access.Scope(db.Deployments.AsNoTracking(), db);
        if (projectId is not null) {
            query = query.Where(item => item.ProjectId == projectId);
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DeploymentStatus>(status, true, out var parsed)) {
            query = query.Where(item => item.Status == parsed);
        }

        var items = await query.OrderByDescending(item => item.RequestedAt).Take(100).Select(item => new {
            item.Id,
            item.ProjectId,
            item.EnvironmentId,
            item.ArtifactId,
            status = item.Status.ToString(),
            item.ApprovalsRequired,
            item.ApprovalsReceived,
            item.RequestedAt,
            item.FailureReason,
        }).ToListAsync(http.RequestAborted);
        return Results.Ok(items);
    }

    // Only actionable rows are returned; the same access rules guard the decision and live hint.
    public static async Task<IResult> ApprovalQueue(ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http) {
        if (principal.FindFirst("actor_type") is not null) return DeploymentAccess.Forbidden();
        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null) return actor.Error;
        var items = new List<object>();
        var query = access.Scope(db.Deployments.AsNoTracking(), db)
            .Where(item => item.Status == DeploymentStatus.AwaitingApproval && item.RequestedById != actor.UserId)
            .OrderByDescending(item => item.RequestedAt);
        for (var offset = 0; items.Count < 100; offset += 100) {
            var batch = await query.Skip(offset).Take(100).ToListAsync(http.RequestAborted);
            foreach (var item in batch) {
                if (!await access.CanApproveAsync(item, db, http.RequestAborted) ||
                    await db.Approvals.AsNoTracking().AnyAsync(approval => approval.DeploymentId == item.Id && approval.ApproverId == actor.UserId, http.RequestAborted)) continue;
                items.Add(new { item.Id, item.ProjectId, item.RequestedAt, status = item.Status.ToString() });
                if (items.Count == 100) break;
            }
            if (batch.Count < 100) break;
        }
        return Results.Ok(items);
    }

    public static async Task<IResult> Get(Guid id, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http) {
        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        var item = await access.Scope(db.Deployments.AsNoTracking(), db).SingleOrDefaultAsync(deployment => deployment.Id == id, http.RequestAborted);
        if (item is null) {
            return Results.Problem(title: "Deployment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new {
            item.Id,
            item.ProjectId,
            item.EnvironmentId,
            item.ArtifactId,
            status = item.Status.ToString(),
            item.ApprovalsRequired,
            item.ApprovalsReceived,
            item.RequestedAt,
            item.StartedAt,
            item.CompletedAt,
            item.FailureReason,
            item.Reason,
            item.PreviousArtifactId,
            item.PolicyEvaluationId,
        });
    }

    public static async Task<IResult> Events(Guid id, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http) {
        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        if (!await access.Scope(db.Deployments.AsNoTracking(), db).AnyAsync(item => item.Id == id, http.RequestAborted)) return DeploymentAccess.NotFound();
        var events = await db.DeploymentEvents.AsNoTracking()
            .Where(item => item.DeploymentId == id)
            .OrderBy(item => item.Sequence)
            .Select(item => new { item.Sequence, item.Timestamp, type = item.Type.ToString(), item.Message })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(events);
    }

    public static async Task<IResult> Evaluation(Guid id, ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, HttpContext http) {
        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        if (!await access.Scope(db.Deployments.AsNoTracking(), db).AnyAsync(item => item.Id == id, http.RequestAborted)) return DeploymentAccess.NotFound();
        var evaluation = await db.PolicyEvaluations.AsNoTracking()
            .Where(item => item.DeploymentId == id)
            .OrderByDescending(item => item.EvaluatedAt)
            .FirstOrDefaultAsync(http.RequestAborted);
        if (evaluation is null) {
            return Results.Problem(title: "Evaluation was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new {
            evaluation.Id,
            decision = evaluation.Decision.ToString(),
            evaluation.ApprovalsRequired,
            evaluation.EvaluatedAt,
            ruleResults = JsonDocument.Parse(evaluation.RuleResults).RootElement.Clone(),
        });
    }

    public static async Task<IResult> Decide(
        Guid id,
        DeploymentDecisionRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        if (!Enum.TryParse<ApprovalDecision>(request.Decision, true, out var decision)) {
            return Results.Problem(title: "Decision must be Approved or Rejected.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null || actor.UserId is null) {
            return actor.Error ?? Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var cancellationToken = http.RequestAborted;
        var deployment = await db.Deployments.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (deployment is null) {
            return Results.Problem(title: "Deployment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        if (!await access.CanSeeAsync(deployment.ProjectId, db, cancellationToken)) {
            await AuditDeniedAsync(db, time, actor, deployment, "deployment.decision_denied", cancellationToken);
            return DeploymentAccess.NotFound();
        }
        if (deployment.RequestedById == actor.UserId ||
            !await access.CanApproveAsync(deployment, db, cancellationToken)) {
            await AuditDeniedAsync(db, time, actor, deployment, "deployment.decision_denied", cancellationToken);
            return DeploymentAccess.Forbidden();
        }
        if (deployment.Status != DeploymentStatus.AwaitingApproval) return Results.Problem(title: "Deployment is not awaiting approval.", statusCode: StatusCodes.Status409Conflict);
        if (await db.Approvals.AnyAsync(item => item.DeploymentId == id && item.ApproverId == actor.UserId, cancellationToken)) return Results.Problem(title: "Approval already recorded.", statusCode: StatusCodes.Status409Conflict);

        var now = time.GetUtcNow();
        db.Approvals.Add(Approval.Create(deployment.Id, actor.UserId.Value, decision, now, request.Comment));
        var sequence = await db.DeploymentEvents.CountAsync(item => item.DeploymentId == deployment.Id, cancellationToken) + 1;
        if (decision == ApprovalDecision.Rejected) {
            deployment.Reject(request.Comment, now);
            db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, sequence, now, DeploymentEventType.Rejected, request.Comment ?? "Rejected."));
        } else {
            deployment.ReceiveApproval();
            db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, sequence, now, DeploymentEventType.ApprovalReceived, "Approval received."));
            db.Jobs.Add(Job.Enqueue(JobType.EvaluateDeployment, JsonSerializer.Serialize(new { deploymentId = deployment.Id }), now, deployment.CorrelationId));
        }

        db.AuditEvents.Add(AuditEvent.Record(now, "User", actor.UserId, actor.Display, "deployment." + decision.ToString().ToLowerInvariant(), "Deployment", deployment.Id, decision.ToString(), deployment.CorrelationId));
        await db.SaveChangesAsync(cancellationToken);
        await DeploymentUpdates.PublishAsync(http.RequestServices.GetService<IConnectionMultiplexer>(), deployment.Id);
        return Results.NoContent();
    }

    public static async Task<IResult> Cancel(
        Guid id,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        var actor = await ActorResult.ResolveAsync(principal, users);
        if (actor.Error is not null) {
            return actor.Error;
        }

        var deployment = await db.Deployments.SingleOrDefaultAsync(item => item.Id == id, http.RequestAborted);
        if (deployment is null) {
            return Results.Problem(title: "Deployment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var access = await DeploymentAccess.ResolveAsync(principal, users);
        if (access.Error is not null) return access.Error;
        if (!await access.CanSeeAsync(deployment.ProjectId, db, http.RequestAborted)) {
            await AuditDeniedAsync(db, time, actor, deployment, "deployment.cancel_denied", http.RequestAborted);
            return DeploymentAccess.NotFound();
        }
        if (!await access.CanCancelAsync(deployment, db, http.RequestAborted)) {
            await AuditDeniedAsync(db, time, actor, deployment, "deployment.cancel_denied", http.RequestAborted);
            return DeploymentAccess.Forbidden();
        }
        if (deployment.Status is not DeploymentStatus.Requested and not DeploymentStatus.AwaitingApproval) return Results.Problem(title: "Deployment cannot be cancelled.", statusCode: StatusCodes.Status409Conflict);

        var now = time.GetUtcNow();
        deployment.Cancel(now);
        var sequence = await db.DeploymentEvents.CountAsync(item => item.DeploymentId == deployment.Id, http.RequestAborted) + 1;
        db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, sequence, now, DeploymentEventType.Cancelled, "Deployment cancelled."));
        db.AuditEvents.Add(AuditEvent.Record(now, actor.Type, actor.UserId ?? actor.ApiKeyId, actor.Display, "deployment.cancelled", "Deployment", deployment.Id, "Cancelled", deployment.CorrelationId));
        await db.SaveChangesAsync(http.RequestAborted);
        await DeploymentUpdates.PublishAsync(http.RequestServices.GetService<IConnectionMultiplexer>(), deployment.Id);
        return Results.NoContent();
    }

    private static async Task<IResult?> AuthorizeTargetAsync(
        ActorResult actor,
        UserManager<User> users,
        AegisOpsDbContext db,
        Guid projectId,
        AegisOps.Domain.Organization.Environment environment,
        CancellationToken cancellationToken
    ) {
        if (actor.ApiKeyId is not null) {
            if (actor.ApiKeyProjectId != projectId) {
                return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
            }

            if (environment.Tier == EnvironmentTier.Production && !environment.Target.AllowApiKeyProduction) {
                return Results.Problem(title: "API key deployments to production are not allowed for this target.", statusCode: StatusCodes.Status403Forbidden);
            }

            return null;
        }

        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var user = await users.FindByIdAsync(actor.UserId!.Value.ToString());
        if (user is null || !await users.IsInRoleAsync(user, "Admin") && !await db.TeamMembers.AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == actor.UserId.Value, cancellationToken)) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        return null;
    }

    private static async Task AuditDeniedAsync(AegisOpsDbContext db, TimeProvider time, ActorResult actor, Deployment deployment, string action, CancellationToken token) {
        db.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor.Type, actor.UserId ?? actor.ApiKeyId, actor.Display,
            action, "Deployment", deployment.Id, "Denied", deployment.CorrelationId));
        await db.SaveChangesAsync(token);
    }

    private static Deployment Enqueue(
        AegisOpsDbContext db,
        AegisOps.Domain.Security.Artifact artifact,
        AegisOps.Domain.Organization.Environment environment,
        DateTimeOffset now,
        ActorResult actor,
        string? reason,
        Guid? previousArtifactId
    ) {
        var deployment = Deployment.Request(
            artifact.ProjectId,
            environment.Id,
            artifact.Id,
            now,
            Guid.CreateVersion7().ToString("N"),
            actor.UserId,
            actor.ApiKeyId,
            reason,
            previousArtifactId
        );
        db.Deployments.Add(deployment);
        db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, 1, now, DeploymentEventType.Requested, reason is not null && reason.StartsWith("rollback of ", StringComparison.Ordinal) ? reason : "Deployment requested."));
        db.Jobs.Add(Job.Enqueue(
            JobType.EvaluateDeployment,
            JsonSerializer.Serialize(new { deploymentId = deployment.Id }),
            now,
            deployment.CorrelationId
        ));
        db.AuditEvents.Add(AuditEvent.Record(now, actor.Type, actor.UserId ?? actor.ApiKeyId, actor.Display, "deployment.requested", "Deployment", deployment.Id, "Accepted", deployment.CorrelationId));
        return deployment;
    }
}

public sealed record ActorResult(Guid? UserId, Guid? ApiKeyId, Guid? ApiKeyProjectId, string Type, string Display, IResult? Error) {
    public static async Task<ActorResult> ResolveAsync(ClaimsPrincipal principal, UserManager<User> users) {
        var actorType = principal.FindFirst("actor_type")?.Value;
        if (string.Equals(actorType, "apiKey", StringComparison.OrdinalIgnoreCase)) {
            var subject = principal.FindFirst("sub")?.Value;
            var project = principal.FindFirst("project")?.Value;
            const string prefix = "apikey:";
            if (subject is null || !subject.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(subject[prefix.Length..], out var apiKeyId) || !Guid.TryParse(project, out var projectId)) {
                return new ActorResult(null, null, null, "ApiKey", "api-key", Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
            }

            return new ActorResult(null, apiKeyId, projectId, "ApiKey", "api-key", null);
        }

        var userSubject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userSubject)) {
            return new ActorResult(null, null, null, "User", "", Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        }

        var user = await users.FindByIdAsync(userSubject);
        if (user is null || !user.IsActive || !Guid.TryParse(userSubject, out var userId)) {
            return new ActorResult(null, null, null, "User", "", Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        }

        return new ActorResult(userId, null, null, "User", user.Email ?? user.DisplayName, null);
    }
}
