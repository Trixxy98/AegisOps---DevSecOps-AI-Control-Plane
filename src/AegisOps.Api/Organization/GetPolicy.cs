using AegisOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Organization;

public static class GetPolicy {
    public static async Task<IResult> Handle(
        Guid id,
        AegisOpsDbContext db,
        HttpContext http
    ) {
        var cancellationToken = http.RequestAborted;
        var policy = await db.Policies
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (policy is null) {
            return Results.Problem(
                title: "Policy was not found.",
                statusCode: StatusCodes.Status404NotFound
            );
        }

        var rules = await db.PolicyRules
            .AsNoTracking()
            .Where(rule => rule.PolicyId == policy.Id)
            .ToListAsync(cancellationToken);

        return Results.Ok(CreatePolicy.ToResponse(policy, rules));
    }
}