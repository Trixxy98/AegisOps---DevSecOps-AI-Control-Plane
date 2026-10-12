using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisOps.Domain.Security;

namespace AegisOps.Application.Scanning;

public sealed record ParsedFinding(
    Severity Severity,
    string RuleId,
    string Title,
    string Fingerprint,
    string? Description,
    string? FilePath,
    int? StartLine,
    string? PackageName,
    string? InstalledVersion,
    string? FixedVersion,
    string? Cve,
    string? HelpUri
);

public static class SarifReportParser {
    public const int MaxFindings = 10_000;

    private static readonly JsonDocumentOptions Json = new() {
        MaxDepth = 64,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<ParsedFinding> Parse(ScannerType scanner, ReadOnlyMemory<byte> report) {
        using var document = JsonDocument.Parse(report, Json);
        if (!document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) {
            return [];
        }

        var findings = new List<ParsedFinding>();
        foreach (var run in runs.EnumerateArray()) {
            var rules = IndexRules(run);
            if (!run.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) {
                continue;
            }

            foreach (var result in results.EnumerateArray()) {
                if (findings.Count >= MaxFindings) {
                    return findings;
                }

                findings.Add(ReadResult(scanner, result, rules));
            }
        }

        return findings;
    }

    private static Dictionary<string, JsonElement> IndexRules(JsonElement run) {
        var rules = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!run.TryGetProperty("tool", out var tool)
            || !tool.TryGetProperty("driver", out var driver)
            || !driver.TryGetProperty("rules", out var items)
            || items.ValueKind != JsonValueKind.Array) {
            return rules;
        }

        foreach (var rule in items.EnumerateArray()) {
            var id = Text(rule, "id");
            if (!string.IsNullOrWhiteSpace(id)) {
                rules[id] = rule;
            }
        }

        return rules;
    }

    private static ParsedFinding ReadResult(ScannerType scanner, JsonElement result, Dictionary<string, JsonElement> rules) {
        var ruleId = Text(result, "ruleId") ?? "unknown";
        rules.TryGetValue(ruleId, out var rule);
        var description = Text(result, "message", "text");
        var title = Text(rule, "shortDescription", "text")
            ?? FirstLine(description)
            ?? ruleId;
        var (filePath, startLine) = Location(result);
        var package = Field(description, "Package");
        var installed = Field(description, "Installed Version");
        var fixedVersion = Field(description, "Fixed Version");
        var cve = ruleId.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)
            ? ruleId
            : Field(description, "CVE") ?? FindCve(description);
        var helpUri = Text(rule, "helpUri");
        var severity = MapSeverity(scanner, result, rule);
        var fingerprint = Fingerprint(scanner, result, ruleId, filePath, package, installed);
        return new ParsedFinding(severity, ruleId, title, fingerprint, description, filePath, startLine, package, installed, fixedVersion, cve, helpUri);
    }

    private static Severity MapSeverity(ScannerType scanner, JsonElement result, JsonElement rule) {
        var score = Score(result) ?? Score(rule);
        if (scanner == ScannerType.Gitleaks) {
            return Severity.Critical;
        }

        if (scanner == ScannerType.Trivy) {
            if (score is double value) {
                return FromScore(value);
            }

            return FromTag(Tags(rule)) ?? Severity.Unknown;
        }

        var level = Text(result, "level");
        var mapped = level switch {
            "error" => Severity.High,
            "warning" => Severity.Medium,
            "note" => Severity.Low,
            "none" => Severity.Info,
            _ => Severity.Unknown,
        };
        return score is >= 9.0 ? Severity.Critical : mapped;
    }

    private static Severity FromScore(double score) {
        if (score >= 9.0) {
            return Severity.Critical;
        }

        if (score >= 7.0) {
            return Severity.High;
        }

        if (score >= 4.0) {
            return Severity.Medium;
        }

        if (score > 0) {
            return Severity.Low;
        }

        return Severity.Unknown;
    }

    private static Severity? FromTag(IEnumerable<string> tags) {
        foreach (var tag in tags) {
            if (Enum.TryParse<Severity>(tag, ignoreCase: true, out var severity) && severity != Severity.Unknown) {
                return severity;
            }
        }

        return null;
    }

    private static double? Score(JsonElement element) {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty("security-severity", out var score)) {
            return null;
        }

        if (score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var number)) {
            return number;
        }

        if (score.ValueKind == JsonValueKind.String && double.TryParse(score.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)) {
            return parsed;
        }

        return null;
    }

    private static IEnumerable<string> Tags(JsonElement rule) {
        if (rule.ValueKind != JsonValueKind.Object
            || !rule.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty("tags", out var tags)
            || tags.ValueKind != JsonValueKind.Array) {
            yield break;
        }

        foreach (var tag in tags.EnumerateArray()) {
            if (tag.ValueKind == JsonValueKind.String && tag.GetString() is string value) {
                yield return value;
            }
        }
    }

    private static (string? FilePath, int? StartLine) Location(JsonElement result) {
        if (!result.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array) {
            return (null, null);
        }

        foreach (var location in locations.EnumerateArray()) {
            if (!location.TryGetProperty("physicalLocation", out var physical)) {
                continue;
            }

            var path = Text(physical, "artifactLocation", "uri");
            int? line = null;
            if (physical.TryGetProperty("region", out var region)
                && region.TryGetProperty("startLine", out var start)
                && start.TryGetInt32(out var number)) {
                line = number;
            }

            return (path, line);
        }

        return (null, null);
    }

    private static string Fingerprint(ScannerType scanner, JsonElement result, string ruleId, string? filePath, string? package, string? installed) {
        var provided = FirstString(result, "partialFingerprints") ?? FirstString(result, "fingerprints");
        if (!string.IsNullOrWhiteSpace(provided)) {
            return provided;
        }

        var raw = $"{scanner}|{ruleId}|{filePath}|{package}|{installed}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static string? FirstString(JsonElement element, string property) {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object) {
            return null;
        }

        foreach (var item in value.EnumerateObject()) {
            if (item.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.Value.GetString())) {
                return item.Value.GetString();
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string property) {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? Text(JsonElement element, string property, string nested) {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) {
            return null;
        }

        return Text(value, nested);
    }

    private static string? FirstLine(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var line = text.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? null : line;
    }

    private static string? Field(string? text, string label) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        foreach (var line in text.Split('\n')) {
            var trimmed = line.Trim();
            var prefix = label + ":";
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                var value = trimmed[prefix.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? FindCve(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        var start = text.IndexOf("CVE-", StringComparison.OrdinalIgnoreCase);
        if (start < 0) {
            return null;
        }

        var end = start + 4;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '-')) {
            end++;
        }

        return text[start..end];
    }
}
