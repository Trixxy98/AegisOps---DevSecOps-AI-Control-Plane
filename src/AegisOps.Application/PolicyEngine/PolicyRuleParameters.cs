using System.Globalization;
using System.Text.Json;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;

namespace AegisOps.Application.PolicyEngine;

/// <summary>A save-time problem with one rule's parameters. <see cref="Field"/> is the JSON path, e.g. <c>allowed[1].to</c>.</summary>
public sealed record PolicyParameterProblem(string Field, string Message);

/// <summary>
/// Save-time validation of rule parameters per <see cref="PolicyRuleType"/> (docs/05 §4). It mirrors what
/// <see cref="PolicyEngine"/> reads: property names are case-insensitive like the engine's deserializer, unknown
/// properties are rejected (a typo such as <c>maxAgeDays</c> would otherwise be ignored silently), and optional
/// properties may be omitted or <c>null</c> to get the engine's default. Only writes are validated; stored rules that
/// predate this check still load and evaluate (fail closed).
/// </summary>
public static class PolicyRuleParameters {
    public const int MinApprovals = 1;
    public const int MaxApprovals = 10;
    public const int MaxFindingsLimit = 10_000;
    public const int MinAgeHours = 1;
    public const int MaxAgeHours = 8760;
    public const int MaxWindowSlots = 50;
    public const string DefaultTimezone = "Asia/Kuala_Lumpur";

    public static IReadOnlyList<string> Scanners { get; } = Enum.GetNames<ScannerType>();

    /// <summary>Severities the engine can rank (<c>Unknown</c> is counted through <c>includeUnknown</c> instead).</summary>
    public static IReadOnlyList<string> Severities { get; } = ["Critical", "High", "Medium", "Low", "Info"];

    public static IReadOnlyList<string> Days { get; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static IReadOnlyList<string> Tiers { get; } = Enum.GetNames<EnvironmentTier>();

    /// <summary>Roles that hold <c>deployments:approve</c>; approval role names are compared case-sensitively.</summary>
    public static IReadOnlyList<string> ApproverRoles { get; } = RolePermissions.Roles
        .Where(role => RolePermissions.Grants([role], Permissions.DeploymentsApprove))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static readonly Dictionary<PolicyRuleType, string[]> Allowed = new() {
        [PolicyRuleType.RequireTestsPassed] = [],
        [PolicyRuleType.RequireImageDigest] = [],
        [PolicyRuleType.RequireScan] = ["scanners", "maxAgeHours"],
        [PolicyRuleType.MaxFindings] = ["scanner", "severity", "max", "includeUnknown"],
        [PolicyRuleType.RequireApprovals] = ["count", "roles"],
        [PolicyRuleType.AllowedBranches] = ["patterns"],
        [PolicyRuleType.DeploymentWindow] = ["timezone", "allowed"],
        [PolicyRuleType.RequirePriorEnvironment] = ["tier", "status", "maxAgeHours"],
    };

    /// <summary>Returns the first problem with <paramref name="parameters"/> for a rule of <paramref name="type"/>, or <c>null</c>.</summary>
    public static PolicyParameterProblem? Validate(PolicyRuleType type, string? parameters) {
        if (!Allowed.TryGetValue(type, out var names)) {
            return new("type", "Rule type is invalid.");
        }

        JsonDocument document;
        try {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(parameters) ? "{}" : parameters);
        } catch (JsonException) {
            return new("parameters", "Parameters must be a JSON object.");
        }

        using (document) {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) {
                return new("parameters", "Parameters must be a JSON object.");
            }

            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject()) {
                var known = names.FirstOrDefault(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (known is null) {
                    return new(property.Name, names.Length == 0
                        ? $"{type} takes no parameters; remove \"{property.Name}\"."
                        : $"\"{property.Name}\" is not a {type} parameter (allowed: {string.Join(", ", names)}).");
                }

                if (!values.TryAdd(known, property.Value)) {
                    return new(known, $"\"{known}\" is given more than once.");
                }
            }

            return type switch {
                PolicyRuleType.RequireScan => RequireScan(values),
                PolicyRuleType.MaxFindings => MaxFindings(values),
                PolicyRuleType.RequireApprovals => RequireApprovals(values),
                PolicyRuleType.AllowedBranches => AllowedBranches(values),
                PolicyRuleType.DeploymentWindow => DeploymentWindow(values),
                PolicyRuleType.RequirePriorEnvironment => RequirePriorEnvironment(values),
                _ => null,
            };
        }
    }

    private static PolicyParameterProblem? RequireScan(Dictionary<string, JsonElement> values) {
        if (!values.TryGetValue("scanners", out var scanners) || scanners.ValueKind != JsonValueKind.Array || scanners.GetArrayLength() == 0) {
            return new("scanners", $"\"scanners\" must list at least one of {string.Join(", ", Scanners)}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in scanners.EnumerateArray()) {
            var field = Indexed("scanners", index++);
            if (OneOf(item, Scanners) is not { } scanner) {
                return new(field, $"\"{field}\" must be one of {string.Join(", ", Scanners)}.");
            }

            if (!seen.Add(scanner)) {
                return new(field, $"\"{field}\" repeats {scanner}.");
            }
        }

        return AgeHours(values);
    }

    private static PolicyParameterProblem? MaxFindings(Dictionary<string, JsonElement> values) {
        if (values.TryGetValue("scanner", out var scanner) && scanner.ValueKind != JsonValueKind.Null && OneOf(scanner, Scanners) is null) {
            return new("scanner", $"\"scanner\" must be one of {string.Join(", ", Scanners)}, or null for all scanners.");
        }

        if (values.TryGetValue("severity", out var severity) && severity.ValueKind != JsonValueKind.Null && OneOf(severity, Severities) is null) {
            return new("severity", $"\"severity\" must be one of {string.Join(", ", Severities)}.");
        }

        if (values.TryGetValue("max", out var max) && max.ValueKind != JsonValueKind.Null && !InRange(max, 0, MaxFindingsLimit)) {
            return new("max", string.Create(CultureInfo.InvariantCulture, $"\"max\" must be a whole number from 0 to {MaxFindingsLimit}."));
        }

        if (values.TryGetValue("includeUnknown", out var unknown) && unknown.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) {
            return new("includeUnknown", "\"includeUnknown\" must be true or false.");
        }

        return null;
    }

    private static PolicyParameterProblem? RequireApprovals(Dictionary<string, JsonElement> values) {
        // The engine reads count as a non-nullable int: null would make every evaluation of the rule fail.
        if (values.TryGetValue("count", out var count) && !InRange(count, MinApprovals, MaxApprovals)) {
            return new("count", string.Create(CultureInfo.InvariantCulture, $"\"count\" must be a whole number from {MinApprovals} to {MaxApprovals}."));
        }

        if (!values.TryGetValue("roles", out var roles) || roles.ValueKind == JsonValueKind.Null) {
            return null;
        }

        if (roles.ValueKind != JsonValueKind.Array) {
            return new("roles", $"\"roles\" must be a list drawn from {string.Join(", ", ApproverRoles)}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in roles.EnumerateArray()) {
            var field = Indexed("roles", index++);
            // Approval checks compare role names exactly (DeploymentAccess.CanApproveAsync), so no case folding here.
            if (item.ValueKind != JsonValueKind.String || !ApproverRoles.Contains(item.GetString(), StringComparer.Ordinal)) {
                return new(field, $"\"{field}\" must be one of {string.Join(", ", ApproverRoles)}.");
            }

            if (!seen.Add(item.GetString()!)) {
                return new(field, $"\"{field}\" repeats {item.GetString()}.");
            }
        }

        return null;
    }

    private static PolicyParameterProblem? AllowedBranches(Dictionary<string, JsonElement> values) {
        if (!values.TryGetValue("patterns", out var patterns) || patterns.ValueKind != JsonValueKind.Array || patterns.GetArrayLength() == 0) {
            return new("patterns", "AllowedBranches needs a \"patterns\" array of branch globs.");
        }

        if (patterns.GetArrayLength() > BranchGlob.MaxPatterns) {
            return new("patterns", string.Create(CultureInfo.InvariantCulture, $"AllowedBranches accepts at most {BranchGlob.MaxPatterns} patterns."));
        }

        var index = 0;
        foreach (var item in patterns.EnumerateArray()) {
            var field = Indexed("patterns", index++);
            if (item.ValueKind != JsonValueKind.String) {
                return new(field, $"\"{field}\" must be a branch glob string.");
            }

            if (BranchGlob.Validate(item.GetString()) is { } problem) {
                return new(field, problem);
            }
        }

        return null;
    }

    private static PolicyParameterProblem? DeploymentWindow(Dictionary<string, JsonElement> values) {
        if (values.TryGetValue("timezone", out var timezone) && timezone.ValueKind != JsonValueKind.Null) {
            var id = timezone.ValueKind == JsonValueKind.String ? timezone.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || !TimeZoneInfo.TryFindSystemTimeZoneById(id, out _)) {
                return new("timezone", "\"timezone\" must be an IANA time zone such as Asia/Kuala_Lumpur or UTC.");
            }
        }

        if (!values.TryGetValue("allowed", out var allowed) || allowed.ValueKind != JsonValueKind.Array || allowed.GetArrayLength() == 0) {
            return new("allowed", "\"allowed\" must list at least one window ({ days, from, to }).");
        }

        if (allowed.GetArrayLength() > MaxWindowSlots) {
            return new("allowed", string.Create(CultureInfo.InvariantCulture, $"\"allowed\" accepts at most {MaxWindowSlots} windows."));
        }

        var index = 0;
        foreach (var slot in allowed.EnumerateArray()) {
            var prefix = Indexed("allowed", index++);
            if (slot.ValueKind != JsonValueKind.Object) {
                return new(prefix, $"\"{prefix}\" must be an object with days, from and to.");
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in slot.EnumerateObject()) {
                if (property.Name.ToLowerInvariant() is not ("days" or "from" or "to")) {
                    return new($"{prefix}.{property.Name}", $"\"{prefix}.{property.Name}\" is not a window field (allowed: days, from, to).");
                }

                if (!fields.TryAdd(property.Name, property.Value)) {
                    return new($"{prefix}.{property.Name}", $"\"{prefix}.{property.Name}\" is given more than once.");
                }
            }

            if (!fields.TryGetValue("days", out var days) || days.ValueKind != JsonValueKind.Array || days.GetArrayLength() == 0) {
                return new($"{prefix}.days", $"\"{prefix}.days\" must list at least one of {string.Join(", ", Days)}.");
            }

            var dayIndex = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var day in days.EnumerateArray()) {
                var field = Indexed($"{prefix}.days", dayIndex++);
                if (OneOf(day, Days) is not { } name) {
                    return new(field, $"\"{field}\" must be one of {string.Join(", ", Days)}.");
                }

                if (!seen.Add(name)) {
                    return new(field, $"\"{field}\" repeats {name}.");
                }
            }

            var from = Clock(fields, "from");
            if (from is null) {
                return new($"{prefix}.from", $"\"{prefix}.from\" must be a time of day from 00:00 to 23:59 (HH:mm).");
            }

            var to = Clock(fields, "to");
            if (to is null) {
                return new($"{prefix}.to", $"\"{prefix}.to\" must be a time of day from 00:00 to 23:59 (HH:mm).");
            }

            // The engine checks from <= now <= to on the same day; a window that crosses midnight never matches.
            if (to <= from) {
                return new($"{prefix}.to", $"\"{prefix}.to\" must be later than \"{prefix}.from\"; split a window that crosses midnight into two.");
            }
        }

        return null;
    }

    private static PolicyParameterProblem? RequirePriorEnvironment(Dictionary<string, JsonElement> values) {
        if (!values.TryGetValue("tier", out var tier) || OneOf(tier, Tiers) is null) {
            return new("tier", $"\"tier\" must be one of {string.Join(", ", Tiers)}.");
        }

        // The engine only records successful prior deployments; "status" documents that and accepts nothing else.
        if (values.TryGetValue("status", out var status) && status.ValueKind != JsonValueKind.Null &&
            !(status.ValueKind == JsonValueKind.String && string.Equals(status.GetString(), "Succeeded", StringComparison.OrdinalIgnoreCase))) {
            return new("status", "\"status\" must be Succeeded.");
        }

        return AgeHours(values);
    }

    private static PolicyParameterProblem? AgeHours(Dictionary<string, JsonElement> values) {
        if (values.TryGetValue("maxAgeHours", out var age) && age.ValueKind != JsonValueKind.Null && !InRange(age, MinAgeHours, MaxAgeHours)) {
            return new("maxAgeHours", string.Create(CultureInfo.InvariantCulture,
                $"\"maxAgeHours\" must be a whole number from {MinAgeHours} to {MaxAgeHours} (one year), or omitted for no age limit."));
        }

        return null;
    }

    private static TimeSpan? Clock(Dictionary<string, JsonElement> fields, string name) {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String) {
            return null;
        }

        var text = value.GetString();
        // Require h:mm at least, so "9" (nine days to TimeSpan) or "1.02:00" are not read as a clock time.
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (colon < 0 || (dot >= 0 && dot < colon)) {
            return null;
        }

        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed) && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1)
            ? parsed
            : null;
    }

    private static bool InRange(JsonElement value, int min, int max) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max;

    private static string? OneOf(JsonElement value, IReadOnlyList<string> options) {
        if (value.ValueKind != JsonValueKind.String) {
            return null;
        }

        var text = value.GetString();
        return options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
    }

    private static string Indexed(string name, int index) => string.Create(CultureInfo.InvariantCulture, $"{name}[{index}]");
}
