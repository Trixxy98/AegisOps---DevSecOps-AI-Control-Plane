using Microsoft.AspNetCore.Identity;

namespace AegisOps.Api.Identity;

/// <summary>
/// The four global roles of docs/09. <see cref="EnsureAsync"/> is idempotent and is used by the Development seed,
/// the <c>migrate</c> command and <c>bootstrap-admin</c>, so every environment has the same role set.
/// </summary>
public static class IdentityRoles {
    public const string Admin = "Admin";
    public const string Developer = "Developer";
    public const string Security = "Security";
    public const string Approver = "Approver";

    public static IReadOnlyList<string> All { get; } = [Admin, Developer, Security, Approver];

    /// <summary>Creates any missing role; returns the names that were created (empty when all existed).</summary>
    public static async Task<IReadOnlyList<string>> EnsureAsync(RoleManager<IdentityRole<Guid>> roles) {
        ArgumentNullException.ThrowIfNull(roles);
        var created = new List<string>();
        foreach (var name in All) {
            if (await roles.RoleExistsAsync(name)) {
                continue;
            }

            var result = await roles.CreateAsync(new IdentityRole<Guid> {
                Id = Guid.CreateVersion7(),
                Name = name,
            });
            if (!result.Succeeded) {
                throw new InvalidOperationException(string.Join("; ", result.Errors.Select(error => error.Description)));
            }

            created.Add(name);
        }

        return created;
    }
}
