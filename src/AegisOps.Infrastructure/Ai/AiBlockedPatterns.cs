using System.Text.RegularExpressions;

namespace AegisOps.Infrastructure.Ai;

/// <summary>Checks a prompt against an AI policy's blocked patterns.</summary>
public interface IBlockedPatternMatcher {
    /// <summary>
    /// True when any pattern matches. Throws <see cref="RegexMatchTimeoutException"/> on timeout and
    /// <see cref="ArgumentException"/>/<see cref="NotSupportedException"/> for a pattern that cannot be compiled;
    /// the gateway treats all of them as a failure and blocks the request.
    /// </summary>
    bool IsBlocked(string input, IReadOnlyList<string> patterns);
}

/// <summary>
/// AI policy <c>BlockedPatterns</c> (docs/08, docs/17 §3): .NET regular expressions compiled with
/// <see cref="RegexOptions.NonBacktracking"/> (matching is linear in the prompt length, so a pattern cannot cause
/// catastrophic backtracking), case-insensitive, culture-invariant and with a match timeout. Constructs that
/// NonBacktracking does not support (backreferences, lookarounds, atomic groups, conditionals) are rejected when the
/// policy is saved.
/// </summary>
public sealed class AiBlockedPatterns : IBlockedPatternMatcher {
    public const int MaxPatterns = 20;
    public const int MaxPatternLength = 256;
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(100);
    internal const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private readonly TimeSpan timeout;

    public AiBlockedPatterns() : this(DefaultMatchTimeout) { }

    public AiBlockedPatterns(TimeSpan matchTimeout) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(matchTimeout, TimeSpan.Zero);
        timeout = matchTimeout;
    }

    public bool IsBlocked(string input, IReadOnlyList<string> patterns) {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(patterns);
        // Static Regex methods reuse a small internal cache of compiled patterns.
        return patterns.Any(pattern => Regex.IsMatch(input, pattern, Options, timeout));
    }

    /// <summary>Returns a problem description for the first invalid pattern, or <c>null</c> when all are valid.</summary>
    public static string? Validate(IReadOnlyList<string?>? patterns) {
        if (patterns is null) return "Blocked patterns are required (use an empty list for none).";
        if (patterns.Count > MaxPatterns) return $"At most {MaxPatterns} blocked patterns are allowed.";
        for (var index = 0; index < patterns.Count; index++) {
            var pattern = patterns[index];
            var position = index + 1;
            if (string.IsNullOrEmpty(pattern)) return $"Blocked pattern {position} is empty.";
            if (pattern.Length > MaxPatternLength) return $"Blocked pattern {position} is longer than {MaxPatternLength} characters.";
            try {
                _ = new Regex(pattern, Options, DefaultMatchTimeout);
            } catch (NotSupportedException) {
                return $"Blocked pattern {position} uses a construct that linear-time matching does not support " +
                    "(backreferences, lookahead/lookbehind, atomic groups or conditionals), or is too large.";
            } catch (ArgumentException) {
                return $"Blocked pattern {position} is not a valid regular expression.";
            }
        }
        return null;
    }
}
