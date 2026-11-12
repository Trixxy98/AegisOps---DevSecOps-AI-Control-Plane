using System.Text.Json;
using System.Text.RegularExpressions;
using AegisOps.Domain.Policy;

namespace AegisOps.Application.PolicyEngine;

/// <summary>
/// Branch globs for <see cref="PolicyRuleType.AllowedBranches"/> (docs/05, docs/17 §3). <c>*</c> matches any run of
/// characters; everything else is literal. Globs become anchored regular expressions compiled with
/// <see cref="RegexOptions.NonBacktracking"/> (linear time in the branch length) and a match timeout.
/// </summary>
public static class BranchGlob {
    public const int MaxPatterns = 50;
    public const int MaxPatternLength = 255;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    internal const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The anchored regular expression for a glob.</summary>
    public static string ToRegex(string glob) {
        ArgumentNullException.ThrowIfNull(glob);
        return "^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
    }

    /// <summary>Matches a branch against a glob. Throws <see cref="RegexMatchTimeoutException"/> on timeout.</summary>
    public static bool IsMatch(string branch, string glob) =>
        Regex.IsMatch(branch, ToRegex(glob), Options, MatchTimeout);

    /// <summary>Returns a problem description for an invalid glob, or <c>null</c> when it is valid.</summary>
    public static string? Validate(string? glob) {
        if (string.IsNullOrWhiteSpace(glob)) return "Branch patterns must not be empty.";
        if (glob.Length > MaxPatternLength) return $"Branch patterns must be at most {MaxPatternLength} characters.";
        if (glob.Any(char.IsControl)) return "Branch patterns must not contain control characters.";
        try {
            _ = new Regex(ToRegex(glob), Options, MatchTimeout);
        } catch (ArgumentException) {
            return $"Branch pattern '{glob}' is invalid.";
        } catch (NotSupportedException) {
            return $"Branch pattern '{glob}' is invalid.";
        }
        return null;
    }

    /// <summary>
    /// Validates rule parameters at policy save time so a bad rule is rejected with 400 instead of failing every
    /// later evaluation. Only <see cref="PolicyRuleType.AllowedBranches"/> is checked here.
    /// </summary>
    /// <exception cref="ArgumentException">The parameters are invalid; the message is safe to return to the caller.</exception>
    public static void ValidateRuleParameters(PolicyRuleType type, string? parameters) {
        if (type != PolicyRuleType.AllowedBranches) return;
        string[]? patterns;
        try {
            patterns = string.IsNullOrWhiteSpace(parameters)
                ? null
                : JsonSerializer.Deserialize<BranchParameters>(parameters, Json)?.Patterns;
        } catch (JsonException exception) {
            throw new ArgumentException("AllowedBranches needs a \"patterns\" array of branch globs.", nameof(parameters), exception);
        }
        if (patterns is null || patterns.Length == 0) {
            throw new ArgumentException("AllowedBranches needs a \"patterns\" array of branch globs.", nameof(parameters));
        }
        if (patterns.Length > MaxPatterns) {
            throw new ArgumentException($"AllowedBranches accepts at most {MaxPatterns} patterns.", nameof(parameters));
        }
        foreach (var pattern in patterns) {
            var problem = Validate(pattern);
            if (problem is not null) throw new ArgumentException(problem, nameof(parameters));
        }
    }

    private sealed record BranchParameters(string[]? Patterns);
}
