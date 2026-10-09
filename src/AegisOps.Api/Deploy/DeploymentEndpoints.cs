using System.Security.Claims;
using System.Text.Json;
using AegisOps.Domain.Audit;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Deploy;

public sealed record RequestDeploymentRequest(Guid ArtifactId, Guid EnvironmentId, string? Reason);

public sealed record DeploymentDecisionRequest(string Decision, string? Comment);

public static class DeploymentEndpoints {
    public static async Task<IResult> Request(
        RequestDeploymentRequest request,
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

        var cancellationToken = http.RequestAborted;
        var artifact = await db.Artifacts.SingleOrDefaultAsync(item => item.Id == request.ArtifactId, cancellationToken);
        var environment = await db.Environments.SingleOrDefaultAsync(item => item.Id == request.EnvironmentId, cancellationToken);
        if (artifact is null || environment is null || artifact.ProjectId != environment.ProjectId) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        if (actor.ApiKeyProjectId is Guid projectId && projectId != artifact.ProjectId) {
            return Results.Problem(title: "Artifact or environment was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        var now = time.GetUtcNow();
        var deployment = Deployment.Request(
            artifact.ProjectId,
            environment.Id,
            artifact.Id,
            now,
            Guid.CreateVersion7().ToString("N"),
            actor.UserId,
            actor.ApiKeyId,
            request.Reason
        );
        db.Deployments.Add(deployment);
        db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, 1, now, DeploymentEventType.Requested, "Deployment requested."));
        db.Jobs.Add(Job.Enqueue(
            JobType.EvaluateDeployment,
            JsonSerializer.Serialize(new { deploymentId = deployment.Id }),
            now,
            deployment.CorrelationId
        ));
        db.AuditEvents.Add(AuditEvent.Record(now, actor.Type, actor.UserId ?? actor.ApiKeyId, actor.Display, "deployment.requested", "Deployment", deployment.Id, "Accepted", deployment.CorrelationId));
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/v1/deployments/{deployment.Id}", new { deployment.Id, status = deployment.Status.ToString() });
    }

    public static async Task<IResult> List(
        AegisOpsDbContext db,
        HttpContext http,
        Guid? projectId = null,
        string? status = null
    ) {
        var query = db.Deployments.AsNoTracking().AsQueryable();
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

    public static async Task<IResult> Get(Guid id, AegisOpsDbContext db, HttpContext http) {
        var item = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(deployment => deployment.Id == id, http.RequestAborted);
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
            item.PolicyEvaluationId,
        });
    }

    public static async Task<IResult> Events(Guid id, AegisOpsDbContext db, HttpContext http) {
        var events = await db.DeploymentEvents.AsNoTracking()
            .Where(item => item.DeploymentId == id)
            .OrderBy(item => item.Sequence)
            .Select(item => new { item.Sequence, item.Timestamp, type = item.Type.ToString(), item.Message })
            .ToListAsync(http.RequestAborted);
        return Results.Ok(events);
    }

    public static async Task<IResult> Evaluation(Guid id, AegisOpsDbContext db, HttpContext http) {
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

        if (deployment.RequestedById == actor.UserId) {
            return Results.Problem(title: "The requester cannot approve their own deployment.", statusCode: StatusCodes.Status400BadRequest);
        }

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

        var now = time.GetUtcNow();
        deployment.Cancel(now);
        var sequence = await db.DeploymentEvents.CountAsync(item => item.DeploymentId == deployment.Id, http.RequestAborted) + 1;
        db.DeploymentEvents.Add(DeploymentEvent.Create(deployment.Id, sequence, now, DeploymentEventType.Cancelled, "Deployment cancelled."));
        db.AuditEvents.Add(AuditEvent.Record(now, actor.Type, actor.UserId ?? actor.ApiKeyId, actor.Display, "deployment.cancelled", "Deployment", deployment.Id, "Cancelled", deployment.CorrelationId));
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
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
