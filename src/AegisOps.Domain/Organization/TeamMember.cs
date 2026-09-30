namespace AegisOps.Domain.Organization;

public sealed class TeamMember {
    public Guid TeamId {get; private set;}
    public Guid UserId {get; private set;}
    public TeamRole Role {get; private set;}
    public DateTimeOffset JoinedAt {get; private set;}

    private TeamMember() {

    }

    public static TeamMember Create(
        Guid teamId,
        Guid userId,
        TeamRole role,
        DateTimeOffset joinedAt
    ) {
        if (teamId == Guid.Empty) {
            throw new ArgumentException("Team ID is required.", nameof(teamId));
        }

        if (userId == Guid.Empty) {
            throw new ArgumentException("User ID is required.", nameof(userId));
        }

        if (!Enum.IsDefined(role)) {
            throw new ArgumentException("Team role is invalid.", nameof(role));
        }

        return new TeamMember {
            TeamId = teamId,
            UserId = userId,
            Role = role,
            JoinedAt = joinedAt,
        };
    }

    public void ChangeRole(TeamRole role) {
        if (!Enum.IsDefined(role)) {
            throw new ArgumentException("Team role is invalid.", nameof(role));
        }

        if (Role == role) {
            return;
        }

        Role = role;
    }
}