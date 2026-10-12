using System.Text;
using AegisOps.Application.Scanning;
using AegisOps.Domain.Security;

namespace AegisOps.Application.Tests;

public sealed class SarifReportParserTests {
    [Fact]
    public void Trivy_critical_cve_is_normalized() {
        const string report = """
            {
              "runs": [{
                "tool": {"driver": {"rules": [{
                  "id": "CVE-2024-0001",
                  "shortDescription": {"text": "openssl overflow"},
                  "helpUri": "https://example.test/cve",
                  "properties": {"security-severity": "9.8", "tags": ["CRITICAL"]}
                }]}},
                "results": [{
                  "ruleId": "CVE-2024-0001",
                  "message": {"text": "Package: libssl\nInstalled Version: 1.1.1\nFixed Version: 3.0.0"},
                  "partialFingerprints": {"primaryLocationLineHash": "abc123"},
                  "locations": [{"physicalLocation": {"artifactLocation": {"uri": "usr/lib/libssl.so"}, "region": {"startLine": 1}}}]
                }]
              }]
            }
            """;

        var finding = SarifReportParser.Parse(ScannerType.Trivy, Encoding.UTF8.GetBytes(report)).Single();
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Equal("libssl", finding.PackageName);
        Assert.Equal("CVE-2024-0001", finding.Cve);
        Assert.Equal("abc123", finding.Fingerprint);
        Assert.Equal(1, finding.StartLine);
    }

    [Fact]
    public void Semgrep_warning_is_medium_and_gitleaks_is_critical() {
        const string report = """
            {"runs":[{"results":[{"ruleId":"sql-concat","level":"warning","message":{"text":"concatenated SQL"}}]}]}
            """;
        var semgrep = SarifReportParser.Parse(ScannerType.Semgrep, Encoding.UTF8.GetBytes(report)).Single();
        var gitleaks = SarifReportParser.Parse(ScannerType.Gitleaks, Encoding.UTF8.GetBytes(report)).Single();
        Assert.Equal(Severity.Medium, semgrep.Severity);
        Assert.Equal(Severity.Critical, gitleaks.Severity);
    }
}
