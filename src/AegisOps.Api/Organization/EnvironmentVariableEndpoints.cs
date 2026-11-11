using System.Security.Claims;
using AegisOps.Application.Audit;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using OrgEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Organization;

public sealed record SetEnvironmentVariableRequest(string? Value, bool? IsSecret);

/// <summary>A variable as the API shows it: secret values are write-only and always null here.</summary>
public sealed record EnvironmentVariableResponse(string Name, bool IsSecret, string? Value, DateTimeOffset UpdatedAt) {
    public static EnvironmentVariableResponse From(EnvironmentVariable variable) =>
        new(variable.Name, variable.IsSecret, variable.IsSecret ? null : variable.Value, variable.UpdatedAt);
}

/// <summary>
/// Per-environment configuration injected into the container at deploy time (docs/06, docs/10, docs/17 §5).
/// Reads follow project visibility (Admin/Security or a team member); changes need environments:write plus
/// Admin or the owning team's Owner, like <see cref="UpdateEnvironment"/>. Values never reach audit rows or logs.
/// </summary>
public static class EnvironmentVariableEndpoints {
    public static async Task<IResult> List(
        string slug,
        Guid envId,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        var cancellationToken = http.RequestAborted;
        var access = await FindAsync(slug, envId, principal, users, db, write: false, cancellationToken);
        if (access.Error is not null) return access.Error;

        var variables = await db.EnvironmentVariables.AsNoTracking()
            .Where(item => item.EnvironmentId == access.Environment!.Id)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return Results.Ok(variables.Select(EnvironmentVariableResponse.From).ToArray());
    }

    public static async Task<IResult> Set(
        string slug,
        Guid envId,
        string name,
        SetEnvironmentVariableRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        EnvironmentSecretProtector secrets,
        TimeProvider time,
        HttpContext http
    ) {
        if (request.Value is null) {
            return Results.Problem(title: "Value is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        try {
            EnvironmentVariable.Validate(name, request.Value);
        } catch (ArgumentException exception) {
            return Results.Problem(title: TitleOf(exception), statusCode: StatusCodes.Status400BadRequest);
        }

        var cancellationToken = http.RequestAborted;
        var access = await FindAsync(slug, envId, principal, users, db, write: true, cancellationToken);
        if (access.Error is not null) return access.Error;
        var environment = access.Environment!;
        var isSecret = request.IsSecret ?? false;
        if (isSecret && !secrets.CanProtect) {
            return Results.Problem(
                title: "Secret storage is not configured.",
                detail: "Set EnvironmentSecrets:Enabled and an encryption key (scripts/gen-secrets.sh) to store secret variables.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var variable = await db.EnvironmentVariables.SingleOrDefaultAsync(
            item => item.EnvironmentId == environment.Id && item.Name == name, cancellationToken);
        if (variable is null) {
            var count = await db.EnvironmentVariables.CountAsync(item => item.EnvironmentId == environment.Id, cancellationToken);
            if (count >= EnvironmentVariable.MaxPerEnvironment) {
                return Results.Problem(
                    title: $"An environment can have at most {EnvironmentVariable.MaxPerEnvironment} variables.",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }

        var now = time.GetUtcNow();
        var userId = access.User!.Id;
        var created = variable is null;
        if (isSecret) {
            var envelope = secrets.Protect(environment.Id, name, request.Value);
            if (variable is null) {
                variable = EnvironmentVariable.CreateSecret(environment.Id, name, envelope, userId, now);
                db.EnvironmentVariables.Add(variable);
            } else {
                variable.SetSecret(envelope, userId, now);
            }
        } else if (variable is null) {
            variable = EnvironmentVariable.CreatePlain(environment.Id, name, request.Value, userId, now);
            db.EnvironmentVariables.Add(variable);
        } else {
            variable.SetPlain(request.Value, userId, now);
        }

        // The audit row names the action and the environment only: never the value (or whether it changed).
        audit.Write(now, userId, access.User.Email ?? access.User.DisplayName,
            "environment.variable_set", "Environment", environment.Id);
        try {
            await db.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateException) when (created) {
            // A concurrent request created the same name first; the unique index kept one row.
            return Results.Problem(title: "The variable was changed concurrently; retry the request.", statusCode: StatusCodes.Status409Conflict);
        }

        var response = EnvironmentVariableResponse.From(variable);
        return created
            ? Results.Created($"/api/v1/projects/{access.ProjectSlug}/environments/{environment.Id:D}/variables/{name}", response)
            : Results.Ok(response);
    }

    public static async Task<IResult> Remove(
        string slug,
        Guid envId,
        string name,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        IAuditWriter audit,
        TimeProvider time,
        HttpContext http
    ) {
        var cancellationToken = http.RequestAborted;
        var access = await FindAsync(slug, envId, principal, users, db, write: true, cancellationToken);
        if (access.Error is not null) return access.Error;
        var environment = access.Environment!;
        var variable = await db.EnvironmentVariables.SingleOrDefaultAsync(
            item => item.EnvironmentId == environment.Id && item.Name == name, cancellationToken);
        if (variable is null) {
            return Results.Problem(title: "Variable was not found.", statusCode: StatusCodes.Status404NotFound);
        }

        db.EnvironmentVariables.Remove(variable);
        audit.Write(time.GetUtcNow(), access.User!.Id, access.User.Email ?? access.User.DisplayName,
            "environment.variable_removed", "Environment", environment.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static string TitleOf(ArgumentException exception) {
        // ArgumentException.Message appends " (Parameter 'x')"; the Problem title shows the sentence only.
        var message = exception.Message;
        var suffix = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return suffix < 0 ? message : message[..suffix];
    }

    private sealed record Access(User? User, OrgEnvironment? Environment, string? ProjectSlug, IResult? Error);

    private static async Task<Access> FindAsync(
        string slug,
        Guid envId,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        bool write,
        CancellationToken cancellationToken
    ) {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!Guid.TryParse(subject, out var userId)) {
            return new Access(null, null, null, Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return new Access(null, null, null, Results.Problem(title: "Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized));
        }

        var notFound = new Access(null, null, null, Results.Problem(title: "Project was not found.", statusCode: StatusCodes.Status404NotFound));
        var normalizedSlug = slug.Trim().ToLowerInvariant();
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(item => item.Slug == normalizedSlug, cancellationToken);
        if (project is null) return notFound;

        bool allowed;
        if (await users.IsInRoleAsync(user, "Admin")) {
            allowed = true;
        } else if (write) {
            // Same rule as UpdateEnvironment: only the owning team's Owner (or an Admin) changes environments.
            allowed = await db.TeamMembers.AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == userId && member.Role == TeamRole.Owner,
                cancellationToken);
        } else {
            // Same visibility as GetProject (docs/09 §6): Security reads every project; others need membership.
            allowed = await users.IsInRoleAsync(user, "Security") || await db.TeamMembers.AnyAsync(
                member => member.TeamId == project.TeamId && member.UserId == userId,
                cancellationToken);
        }

        if (!allowed) return notFound;

        var environment = await db.Environments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == envId && item.ProjectId == project.Id, cancellationToken);
        if (environment is null) {
            return new Access(null, null, null, Results.Problem(title: "Environment was not found.", statusCode: StatusCodes.Status404NotFound));
        }

        return new Access(user, environment, project.Slug, null);
    }
}
