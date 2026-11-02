using System.Text.RegularExpressions;

namespace AegisOps.Infrastructure.Ai;

public sealed record SensitiveInspection(string Redacted, string[] Types, int Redactions);

public interface ISensitiveDataDetector {
    SensitiveInspection Inspect(string input, bool redactPii = false);
}

public sealed class RegexSensitiveDataDetector : ISensitiveDataDetector {
    private readonly (string Type, Regex Pattern, bool Pii)[] patterns = Patterns;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private static readonly (string Type, Regex Pattern, bool Pii)[] Patterns = new[] {
        Pattern("AWS_ACCESS_KEY", @"AKIA[0-9A-Z]{16}"),
        Pattern("AWS_SECRET_KEY", @"(?i:aws[_-]?secret(?:[_-]?access[_-]?key)?)\s*[:=]\s*['\""']?[A-Za-z0-9+/]{40}"),
        Pattern("GITHUB_TOKEN", @"gh[pousr]_[A-Za-z0-9]{36,}"),
        Pattern("GENERIC_API_KEY", @"(?i:api[_-]?key|secret|token)\s*[:=]\s*['\""']?[A-Za-z0-9_-]{20,}"),
        Pattern("JWT", @"eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+"),
        Pattern("PRIVATE_KEY", @"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
        Pattern("CONNECTION_STRING", @"(?i:(?:Password|Pwd)=[^;\s]+;|postgres(?:ql)?://[^\s@]+@[^\s]+)"),
        Pattern("MY_NRIC", @"\b\d{6}-\d{2}-\d{4}\b"),
        Pattern("PHONE_MY", @"(?<!\d)(?:\+?6?01)[0-9]{8,9}\b"),
        Pattern("IPV4_PRIVATE", @"\b(?:10|192\.168|172\.(?:1[6-9]|2[0-9]|3[01]))\.\d{1,3}\.\d{1,3}\b", true),
        Pattern("EMAIL", @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", true),
        Pattern("CREDIT_CARD", @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)"),
    };

    private static (string, Regex, bool) Pattern(string type, string pattern, bool pii = false) =>
        (type, new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout), pii);

    public SensitiveInspection Inspect(string input, bool redactPii = false) {
        var spans = new List<(int Start, int Length, string Type)>();
        foreach (var (type, regex, pii) in patterns) {
            if (pii && !redactPii) continue;
            foreach (Match match in regex.Matches(input)) {
                if (type == "CREDIT_CARD" && !ValidLuhn(match.Value)) continue;
                spans.Add((match.Index, match.Length, type));
            }
        }
        var selected = new List<(int Start, int Length, string Type)>();
        foreach (var span in spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length)) {
            if (selected.Count > 0 && span.Start < selected[^1].Start + selected[^1].Length) continue;
            selected.Add(span);
        }
        var result = new System.Text.StringBuilder();
        var offset = 0;
        foreach (var span in selected) {
            result.Append(input, offset, span.Start - offset);
            result.Append("[REDACTED:").Append(span.Type).Append(']');
            offset = span.Start + span.Length;
        }
        result.Append(input, offset, input.Length - offset);
        return new SensitiveInspection(result.ToString(), selected.Select(s => s.Type).Distinct().ToArray(), selected.Count);
    }

    private static bool ValidLuhn(string value) {
        var digits = value.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        for (var i = digits.Length - 1; i >= 0; i--) {
            var digit = digits[i];
            if ((digits.Length - i) % 2 == 0) { digit *= 2; if (digit > 9) digit -= 9; }
            sum += digit;
        }
        return sum % 10 == 0;
    }
}
