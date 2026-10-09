using AegisOps.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisOps.Api.Identity;

public sealed record UserListItem(Guid Id, string Email, string DisplayName, bool IsActive, IList<string> Roles);

public static class ListUsers {
    public static async Task<IResult> Handle(UserManager<User> users) {
        var accounts = await users.Users.OrderBy(item => item.Email).ToListAsync();
        var result = new List<UserListItem>(accounts.Count);
        foreach (var account in accounts) {
            result.Add(new UserListItem(
                account.Id,
                account.Email ?? string.Empty,
                account.DisplayName,
                account.IsActive,
                await users.GetRolesAsync(account)
            ));
        }

        return Results.Ok(result);
    }
}
