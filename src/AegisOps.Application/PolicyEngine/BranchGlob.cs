using System.Text.RegularExpressions;
using AegisOps.Domain.Policy;

namespace AegisOps.Application.PolicyEngine;

/// <summary>
/// Branch globs for <see cref="PolicyRuleType.AllowedBranches"/> (docs/05, docs/17 §3). <c>*</c> matches any run of
/// characters; everything else is literal. Saved rules are checked by <see cref="PolicyRuleParameters"/>. Globs become anchored regular expressions compiled with
/// <see cref="RegexOptions.NonBacktracking"/> (linear time in the branch length) and a match timeout.
/// </summary>
public static class BranchGlob {
    public const int MaxPatterns = 50;
    public const int MaxPatternLength = 255;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    internal const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

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
}
