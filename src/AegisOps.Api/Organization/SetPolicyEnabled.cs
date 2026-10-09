using System.Security.Claims;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public sealed record SetPolicyEnabledRequest(bool IsEnabled);

public static class SetPolicyEnabled {
    public static async Task<IResult> Handle(
        Guid id,
        SetPolicyEnabledRequest request,
        ClaimsPrincipal principal,
        UserManager<User> users,
        AegisOpsDbContext db,
        TimeProvider time,
        HttpContext http
    ) {
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || !Guid.TryParse(subject, out var userId)) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null || !user.IsActive) {
            return Results.Problem(
                title: "Invalid credentials.",
                statusCode: StatusCodes.Status401Unauthorized
            );
        }

        var cancellationToken = http.RequestAborted;
        var policy = await db.Policies.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken
        );

        if (policy is null) {
            return Results.Problem(
                title: "Policy was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        try {
            if (request.IsEnabled) {
                policy.Enable(userId, time.GetUtcNow());
            } else {
                policy.Disable(userId, time.GetUtcNow());
            }

            await db.SaveChangesAsync(cancellationToken);
        } catch (InvalidOperationException exception) {
            return Results.Problem(
                title: exception.Message,
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var rules = await db.PolicyRules
            .AsNoTracking()
            .Where(rule => rule.PolicyId == policy.Id)
            .ToListAsync(cancellationToken);

        return Results.Ok(CreatePolicy.ToResponse(policy, rules));
    }
}