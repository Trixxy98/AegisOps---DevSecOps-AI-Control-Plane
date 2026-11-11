using System.Text.RegularExpressions;

namespace AegisOps.Domain.Organization;

/// <summary>
/// A configuration value the Worker injects into an environment's container at deploy time (docs/06, docs/13).
/// Plain values are stored in <see cref="Value"/>; secret values only as an encrypted envelope in
/// <see cref="ProtectedValue"/> and are never returned by the API (docs/17 §5).
/// </summary>
public sealed partial class EnvironmentVariable {
    public const int MaxNameLength = 128;
    public const int MaxValueLength = 8192;
    public const int MaxPerEnvironment = 50;
    public const int MaxProtectedValueLength = 16384;
    private const string ReservedPrefix = "AEGISOPS_";
    private static readonly string[] ReservedNames = ["DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();

    public Guid Id { get; private set; }
    public Guid EnvironmentId { get; private set; }
    public string Name { get; private set; }
    public string? Value { get; private set; }
    public string? ProtectedValue { get; private set; }
    public bool IsSecret { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedById { get; private set; }

    private EnvironmentVariable() {
        Name = string.Empty;
    }

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Names the executor sets itself (or reserves for the platform); case-insensitive because
    /// .NET configuration keys are.</summary>
    public static bool IsReservedName(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Validates a variable name and value before anything is encrypted or stored.</summary>
    public static void Validate(string name, string value) {
        ValidateName(name);
        ValidateValue(value);
    }

    public static EnvironmentVariable CreatePlain(Guid environmentId, string name, string value, Guid updatedById, DateTimeOffset updatedAt) {
        var variable = Create(environmentId, name, updatedById);
        variable.SetPlain(value, updatedById, updatedAt);
        return variable;
    }

    public static EnvironmentVariable CreateSecret(Guid environmentId, string name, string protectedValue, Guid updatedById, DateTimeOffset updatedAt) {
        var variable = Create(environmentId, name, updatedById);
        variable.SetSecret(protectedValue, updatedById, updatedAt);
        return variable;
    }

    public void SetPlain(string value, Guid updatedById, DateTimeOffset updatedAt) {
        ValidateValue(value);
        ValidateUser(updatedById);
        Value = value;
        ProtectedValue = null;
        IsSecret = false;
        UpdatedById = updatedById;
        UpdatedAt = updatedAt;
    }

    public void SetSecret(string protectedValue, Guid updatedById, DateTimeOffset updatedAt) {
        if (string.IsNullOrWhiteSpace(protectedValue)) {
            throw new ArgumentException("Encrypted value is required.", nameof(protectedValue));
        }

        if (protectedValue.Length > MaxProtectedValueLength) {
            throw new ArgumentException("Encrypted value is too long.", nameof(protectedValue));
        }

        ValidateUser(updatedById);
        Value = null;
        ProtectedValue = protectedValue;
        IsSecret = true;
        UpdatedById = updatedById;
        UpdatedAt = updatedAt;
    }

    private static EnvironmentVariable Create(Guid environmentId, string name, Guid updatedById) {
        if (environmentId == Guid.Empty) {
            throw new ArgumentException("Environment ID is required.", nameof(environmentId));
        }

        ValidateName(name);
        ValidateUser(updatedById);
        return new EnvironmentVariable {
            Id = Guid.CreateVersion7(),
            EnvironmentId = environmentId,
            Name = name,
        };
    }

    private static void ValidateName(string name) {
        if (!IsValidName(name)) {
            throw new ArgumentException(
                "Variable name must start with a letter or underscore and contain only letters, digits or underscores (max 128).",
                nameof(name));
        }

        if (IsReservedName(name)) {
            throw new ArgumentException(
                "Variable name is reserved (DOTNET_ENVIRONMENT, ASPNETCORE_ENVIRONMENT and AEGISOPS_* are set by AegisOps).",
                nameof(name));
        }
    }

    private static void ValidateValue(string value) {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueLength) {
            throw new ArgumentException("Variable value must be at most 8192 characters.", nameof(value));
        }

        if (value.Contains('\0', StringComparison.Ordinal)) {
            throw new ArgumentException("Variable value must not contain NUL characters.", nameof(value));
        }
    }

    private static void ValidateUser(Guid updatedById) {
        if (updatedById == Guid.Empty) {
            throw new ArgumentException("Updating user is required.", nameof(updatedById));
        }
    }
}
