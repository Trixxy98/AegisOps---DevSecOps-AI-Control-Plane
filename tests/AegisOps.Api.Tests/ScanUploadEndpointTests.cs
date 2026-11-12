using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AegisOps.Api.Identity;
using AegisOps.Api.Organization;
using AegisOps.Application.Audit;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Audit;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Scanning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Api.Tests;

/// <summary>
/// Drives the real scan upload, findings and suppression handlers over HTTP with real JWT, API-key and
/// permission authorization (roadmap checks P3-01 upload, P3-02 inline/queued parsing, P3-05 suppressions).
/// </summary>
public sealed class ScanUploadEndpointTests : IDisposable {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _reports = Path.Combine(Path.GetTempPath(), "aegisops-scan-upload-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_reports)) Directory.Delete(_reports, recursive: true);
    }

    // ---------- P3-01: upload contract ----------

    [Fact]
    public async Task UploadReturns201WithSummaryAndStoresReportUnderArtifactDirectory() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();
        var report = Sarif(
            Result("semgrep.sqli", "error", "fp-high"),
            Result("semgrep.weak", "warning", "fp-medium"),
            Result("semgrep.note", "note", "fp-low"));

        using var response = await host.UploadAsync(artifact.Id, admin, report, scanner: "semgrep", toolVersion: "1.2.3");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        var summary = body.GetProperty("summary");
        Assert.Equal((0, 1, 1, 1, 0, 0, 3, 0), (
            summary.GetProperty("critical").GetInt32(), summary.GetProperty("high").GetInt32(),
            summary.GetProperty("medium").GetInt32(), summary.GetProperty("low").GetInt32(),
            summary.GetProperty("info").GetInt32(), summary.GetProperty("unknown").GetInt32(),
            summary.GetProperty("open").GetInt32(), summary.GetProperty("suppressed").GetInt32()));
        Assert.Equal($"/api/v1/artifacts/{artifact.Id}/findings", body.GetProperty("findingsUrl").GetString());
        var scanId = body.GetProperty("id").GetGuid();

        var directory = Path.Combine(_reports, artifact.Id.ToString("N"));
        var file = Assert.Single(Directory.GetFiles(directory));
        Assert.StartsWith("Semgrep-", Path.GetFileName(file), StringComparison.Ordinal);
        Assert.EndsWith(".sarif", file, StringComparison.Ordinal);
        Assert.Equal(report, await File.ReadAllBytesAsync(file, Token));

        await host.WithDbAsync(async db => {
            var scan = await db.SecurityScans.AsNoTracking().SingleAsync(item => item.Id == scanId, Token);
            Assert.Equal((artifact.Id, ScannerType.Semgrep, ScanSource.CiUploaded, ReportFormat.Sarif, "1.2.3", file),
                (scan.ArtifactId, scan.Scanner, scan.Source, scan.ReportFormat, scan.ToolVersion, scan.RawReportPath));
            Assert.Equal(3, await db.SecurityFindings.CountAsync(item => item.ScanId == scanId, Token));
            Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.Action == "scan.uploaded" && item.ResourceId == scanId, Token));
        });

        using var findings = await host.SendAsync(HttpMethod.Get, $"/api/v1/artifacts/{artifact.Id}/findings", admin);
        Assert.Equal(HttpStatusCode.OK, findings.StatusCode);
        var listed = (await ReadJsonAsync(findings)).EnumerateArray().ToArray();
        Assert.Equal(["fp-high", "fp-low", "fp-medium"], listed.Select(item => item.GetProperty("fingerprint").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(listed, item => Assert.Equal("Open", item.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task UploadRejectsMissingFileEmptyFileWrongFormatUnknownScannerAndInvalidSarif() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();

        async Task AssertBadRequest(HttpResponseMessage response, string title) {
            using (response) {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal(title, (await ReadJsonAsync(response)).GetProperty("title").GetString());
            }
        }

        await AssertBadRequest(await host.UploadAsync(artifact.Id, admin, report: null), "Report file is required.");
        await AssertBadRequest(await host.UploadAsync(artifact.Id, admin, report: []), "Report file is required.");
        await AssertBadRequest(await host.UploadAsync(artifact.Id, admin, Sarif(), format: "json"), "Only SARIF reports are accepted.");
        await AssertBadRequest(await host.UploadAsync(artifact.Id, admin, Sarif(), scanner: "snyk"), "Scanner must be Gitleaks, Trivy, or Semgrep.");
        await AssertBadRequest(await host.UploadAsync(artifact.Id, admin, Encoding.UTF8.GetBytes("{\"runs\": [ not json")), "Report is not valid SARIF.");
        using (var notMultipart = await host.SendAsync(HttpMethod.Post, $"/api/v1/artifacts/{artifact.Id}/scans", admin, new { report = "x" }))
            Assert.Equal(HttpStatusCode.BadRequest, notMultipart.StatusCode);
        using (var unknownArtifact = await host.UploadAsync(Guid.NewGuid(), admin, Sarif()))
            Assert.Equal(HttpStatusCode.NotFound, unknownArtifact.StatusCode);

        await host.WithDbAsync(async db => {
            Assert.Equal(0, await db.SecurityScans.CountAsync(Token));
            Assert.Equal(0, await db.SecurityFindings.CountAsync(Token));
        });
    }

    [Fact]
    public async Task InvalidSarifReuploadKeepsThePreviousScanForThatScanner() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();
        using (var first = await host.UploadAsync(artifact.Id, admin, Sarif(Result("r1", "error", "fp-1"))))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var broken = await host.UploadAsync(artifact.Id, admin, Encoding.UTF8.GetBytes("not json at all"));

        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        await host.WithDbAsync(async db => {
            var scan = await db.SecurityScans.AsNoTracking().SingleAsync(Token);
            Assert.Equal(ScanStatus.Completed, scan.Status);
            Assert.Equal("fp-1", (await db.SecurityFindings.AsNoTracking().SingleAsync(Token)).Fingerprint);
        });
    }

    [Fact]
    public async Task UploadOverTwentyMegabytesIsRejectedByTheHandlerSizeCheck() {
        // Handler-level guard (UploadScan.MaxBytes) with the framework's default 128 MB multipart limit.
        await using var host = await ScanHost.StartAsync(_reports, mirrorProgramFormLimit: false);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();

        using var response = await host.UploadAsync(artifact.Id, admin, OversizedReport());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Report must be 20 MB or smaller.", (await ReadJsonAsync(response)).GetProperty("title").GetString());
        Assert.False(Directory.Exists(Path.Combine(_reports, artifact.Id.ToString("N"))));
        await host.WithDbAsync(async db => Assert.Equal(0, await db.SecurityScans.CountAsync(Token)));
    }

    [Fact]
    public async Task UploadOverTwentyMegabytesIsRejectedWith400UnderProgramFormLimits() {
        await using var host = await ScanHost.StartAsync(_reports, mirrorProgramFormLimit: true);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();

        using var response = await host.UploadAsync(artifact.Id, admin, OversizedReport());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadIsAuthorizedForProjectScopedApiKeysAndSecurityOrAdminUsersOnly() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, security) = await host.CreateUserAsync("security@example.test", "Security");
        var (_, developer) = await host.CreateUserAsync("developer@example.test", "Developer");
        var (_, approver) = await host.CreateUserAsync("approver@example.test", "Approver");
        var artifact = await host.CreateArtifactAsync();
        var otherArtifact = await host.CreateArtifactAsync("other-service");
        var scansKey = await host.CreateApiKeyAsync(artifact.ProjectId, adminId, ["scans:write"]);
        var artifactsOnlyKey = await host.CreateApiKeyAsync(artifact.ProjectId, adminId, ["artifacts:write", "deployments:request"]);
        var revokedKey = await host.CreateApiKeyAsync(artifact.ProjectId, adminId, ["scans:write"], revoked: true);

        async Task<HttpStatusCode> Upload(Guid artifactId, string? bearer, ScannerType scanner) {
            using var response = await host.UploadAsync(artifactId, bearer, Sarif(Result("r", "error", "fp")), scanner.ToString());
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await Upload(artifact.Id, scansKey, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Created, await Upload(artifact.Id, security, ScannerType.Trivy));
        Assert.Equal(HttpStatusCode.Created, await Upload(artifact.Id, admin, ScannerType.Gitleaks));
        // A scans:write key for this project cannot reach another project's artifact.
        Assert.Equal(HttpStatusCode.NotFound, await Upload(otherArtifact.Id, scansKey, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Forbidden, await Upload(artifact.Id, artifactsOnlyKey, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Unauthorized, await Upload(artifact.Id, revokedKey, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Forbidden, await Upload(artifact.Id, developer, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Forbidden, await Upload(artifact.Id, approver, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Unauthorized, await Upload(artifact.Id, null, ScannerType.Semgrep));
        Assert.Equal(HttpStatusCode.Unauthorized, await Upload(artifact.Id, "aok_notakey_xxxxxxxxxxxxxxxxxxxx", ScannerType.Semgrep));

        await host.WithDbAsync(async db => {
            var scans = await db.SecurityScans.AsNoTracking().ToListAsync(Token);
            Assert.Equal([ScannerType.Gitleaks, ScannerType.Trivy, ScannerType.Semgrep], scans.Select(scan => scan.Scanner).Order());
            Assert.All(scans, scan => Assert.Equal(artifact.Id, scan.ArtifactId));
            Assert.Empty(await db.SecurityScans.Where(scan => scan.ArtifactId == otherArtifact.Id).ToListAsync(Token));
            var audits = await db.AuditEvents.AsNoTracking().Where(item => item.Action == "scan.uploaded").ToListAsync(Token);
            Assert.Equal(3, audits.Count);
            Assert.Contains(audits, item => item.ActorDisplay == "api-key");
        });
    }

    // ---------- P3-02: inline vs queued parsing ----------

    [Fact]
    public async Task ReportAtTheInlineLimitIsParsedInlineAndOneByteMoreIsQueuedThenParsedByTheJob() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();

        var inline = PaddedSarif(ScanReportStore.InlineLimit, Result("CVE-2024-0001", null, "fp-crit", score: 9.8), Result("CVE-2024-0002", null, "fp-high", score: 7.5));
        Assert.Equal(ScanReportStore.InlineLimit, inline.Length);
        using (var response = await host.UploadAsync(artifact.Id, admin, inline, scanner: "trivy")) {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await ReadJsonAsync(response);
            Assert.Equal("Completed", body.GetProperty("status").GetString());
            Assert.Equal(1, body.GetProperty("summary").GetProperty("critical").GetInt32());
            Assert.Equal(1, body.GetProperty("summary").GetProperty("high").GetInt32());
        }
        await host.WithDbAsync(async db => {
            Assert.Equal(2, await db.SecurityFindings.CountAsync(Token));
            Assert.Equal(0, await db.Jobs.CountAsync(Token));
        });

        var large = PaddedSarif(ScanReportStore.InlineLimit + 1, Result("semgrep.a", "error", "fp-a"), Result("semgrep.b", "note", "fp-b"), Result("semgrep.c", "warning", "fp-c"));
        Assert.Equal(ScanReportStore.InlineLimit + 1, large.Length);
        Guid queuedId;
        using (var response = await host.UploadAsync(artifact.Id, admin, large, scanner: "semgrep")) {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await ReadJsonAsync(response);
            Assert.Equal("Queued", body.GetProperty("status").GetString());
            Assert.Equal(0, body.GetProperty("summary").GetProperty("open").GetInt32());
            queuedId = body.GetProperty("id").GetGuid();
        }

        var jobScanId = await host.WithDbAsync(async db => {
            var scan = await db.SecurityScans.AsNoTracking().SingleAsync(item => item.Id == queuedId, Token);
            Assert.Equal(ScanStatus.Queued, scan.Status);
            Assert.Null(scan.CompletedAt);
            Assert.True(File.Exists(scan.RawReportPath));
            Assert.Equal(0, await db.SecurityFindings.CountAsync(item => item.ScanId == queuedId, Token));
            var job = await db.Jobs.AsNoTracking().SingleAsync(Token);
            Assert.Equal((JobType.ParseScanReport, JobStatus.Pending, queuedId.ToString("N")), (job.Type, job.Status, job.CorrelationId));
            using var payload = JsonDocument.Parse(job.Payload);
            return payload.RootElement.GetProperty("scanId").GetGuid();
        });
        Assert.Equal(queuedId, jobScanId);

        // What the Worker's ParseScanReport job does (DeploymentProcessor -> ScanReportStore.ParseQueuedAsync).
        await host.WithDbAsync(db => ScanReportStore.ParseQueuedAsync(db, jobScanId, DateTimeOffset.UtcNow, Token));

        await host.WithDbAsync(async db => {
            var scan = await db.SecurityScans.AsNoTracking().SingleAsync(item => item.Id == queuedId, Token);
            Assert.Equal(ScanStatus.Completed, scan.Status);
            Assert.NotNull(scan.CompletedAt);
            Assert.Equal((1, 1, 1, 3), (scan.Summary.High, scan.Summary.Medium, scan.Summary.Low, scan.Summary.Open));
            Assert.Equal(["fp-a", "fp-b", "fp-c"],
                (await db.SecurityFindings.AsNoTracking().Where(item => item.ScanId == queuedId).Select(item => item.Fingerprint).ToListAsync(Token)).Order(StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task QueuedScanWhoseStoredReportIsGoneIsMarkedFailed() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();
        Guid scanId;
        using (var response = await host.UploadAsync(artifact.Id, admin, PaddedSarif(ScanReportStore.InlineLimit + 10, Result("r", "error", "fp")))) {
            scanId = (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
        }
        var path = await host.WithDbAsync(async db => (await db.SecurityScans.AsNoTracking().SingleAsync(Token)).RawReportPath!);
        File.Delete(path);

        await host.WithDbAsync(db => ScanReportStore.ParseQueuedAsync(db, scanId, DateTimeOffset.UtcNow, Token));

        await host.WithDbAsync(async db => {
            var scan = await db.SecurityScans.AsNoTracking().SingleAsync(Token);
            Assert.Equal((ScanStatus.Failed, "Stored report was not found."), (scan.Status, scan.ErrorMessage));
        });
    }

    // ---------- P3-05: suppressions ----------

    [Fact]
    public async Task SuppressionEndpointSuppressesExistingFindingsAndLaterScansButNotExpiredOnes() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (securityId, security) = await host.CreateUserAsync("security@example.test", "Security");
        var (_, developer) = await host.CreateUserAsync("developer@example.test", "Developer");
        var artifact = await host.CreateArtifactAsync();
        var otherArtifact = await host.CreateArtifactAsync("other-service");
        var report = Sarif(Result("rule.a", "error", "fp-endpoint"), Result("rule.b", "error", "fp-expired"),
            Result("rule.c", "error", "fp-future"), Result("rule.d", "error", "fp-open"));
        using (var first = await host.UploadAsync(artifact.Id, admin, report)) Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using (var other = await host.UploadAsync(otherArtifact.Id, admin, report)) Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        // Suppression endpoint: authorization and validation.
        var path = "/api/v1/projects/circuit-service/finding-suppressions";
        using (var forbidden = await host.SendAsync(HttpMethod.Post, path, developer, new SuppressFindingRequest("fp-endpoint", "noise", null)))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using (var invalid = await host.SendAsync(HttpMethod.Post, path, security, new SuppressFindingRequest(" ", "noise", null)))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var missing = await host.SendAsync(HttpMethod.Post, "/api/v1/projects/nope/finding-suppressions", security, new SuppressFindingRequest("fp-endpoint", "noise", null)))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using (var created = await host.SendAsync(HttpMethod.Post, path, security, new SuppressFindingRequest(" fp-endpoint ", "Accepted risk", null))) {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.Equal("fp-endpoint", (await ReadJsonAsync(created)).GetProperty("fingerprint").GetString());
        }

        var now = DateTimeOffset.UtcNow;
        await host.WithDbAsync(async db => {
            var suppression = await db.FindingSuppressions.AsNoTracking().SingleAsync(Token);
            Assert.Equal((artifact.ProjectId, "fp-endpoint", "Accepted risk", securityId, (DateTimeOffset?)null),
                (suppression.ProjectId, suppression.Fingerprint, suppression.Reason, suppression.CreatedById, suppression.ExpiresAt));
            // Existing findings of this project are suppressed immediately; the other project is untouched.
            var existing = await db.SecurityFindings.AsNoTracking().SingleAsync(item => item.ArtifactId == artifact.Id && item.Fingerprint == "fp-endpoint", Token);
            Assert.Equal((FindingStatus.Suppressed, "Accepted risk"), (existing.Status, existing.SuppressionReason));
            var foreign = await db.SecurityFindings.AsNoTracking().SingleAsync(item => item.ArtifactId == otherArtifact.Id && item.Fingerprint == "fp-endpoint", Token);
            Assert.Equal(FindingStatus.Open, foreign.Status);
            Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.Action == "finding.suppressed", Token));

            db.FindingSuppressions.AddRange(
                FindingSuppression.Create(artifact.ProjectId, "fp-expired", "Temporary", adminId, now.AddDays(-2), now.AddDays(-1)),
                FindingSuppression.Create(artifact.ProjectId, "fp-future", "Until fix ships", adminId, now.AddDays(-1), now.AddDays(30)));
            await db.SaveChangesAsync(Token);
        });

        // Re-uploading the same report applies the project's active suppressions to the new scan.
        using var reupload = await host.UploadAsync(artifact.Id, admin, report);
        Assert.Equal(HttpStatusCode.Created, reupload.StatusCode);
        var summary = (await ReadJsonAsync(reupload)).GetProperty("summary");
        Assert.Equal((2, 2, 2), (summary.GetProperty("suppressed").GetInt32(), summary.GetProperty("open").GetInt32(), summary.GetProperty("high").GetInt32()));

        using var findings = await host.SendAsync(HttpMethod.Get, $"/api/v1/artifacts/{artifact.Id}/findings", admin);
        var statuses = (await ReadJsonAsync(findings)).EnumerateArray().ToDictionary(
            item => item.GetProperty("fingerprint").GetString()!,
            item => (item.GetProperty("status").GetString(), item.GetProperty("suppressionReason").GetString()));
        Assert.Equal(4, statuses.Count);
        Assert.Equal(("Suppressed", "Accepted risk"), statuses["fp-endpoint"]);
        Assert.Equal(("Suppressed", "Until fix ships"), statuses["fp-future"]);
        Assert.Equal(("Open", (string?)null), statuses["fp-expired"]);
        Assert.Equal(("Open", (string?)null), statuses["fp-open"]);

        // Suppressions are project-scoped: the other project's re-upload stays fully open.
        using var otherReupload = await host.UploadAsync(otherArtifact.Id, admin, report);
        Assert.Equal(0, (await ReadJsonAsync(otherReupload)).GetProperty("summary").GetProperty("suppressed").GetInt32());
    }

    [Fact]
    public async Task SuppressionAppliesToQueuedReportsParsedByTheJob() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();
        using (var suppress = await host.SendAsync(HttpMethod.Post, "/api/v1/projects/circuit-service/finding-suppressions", admin,
            new SuppressFindingRequest("fp-known", "False positive", null)))
            Assert.Equal(HttpStatusCode.OK, suppress.StatusCode);

        Guid scanId;
        using (var response = await host.UploadAsync(artifact.Id, admin,
            PaddedSarif(ScanReportStore.InlineLimit + 1, Result("r1", "error", "fp-known"), Result("r2", "error", "fp-new")))) {
            scanId = (await ReadJsonAsync(response)).GetProperty("id").GetGuid();
        }
        await host.WithDbAsync(db => ScanReportStore.ParseQueuedAsync(db, scanId, DateTimeOffset.UtcNow, Token));

        await host.WithDbAsync(async db => {
            var scan = await db.SecurityScans.AsNoTracking().SingleAsync(Token);
            Assert.Equal((ScanStatus.Completed, 1, 1), (scan.Status, scan.Summary.Suppressed, scan.Summary.Open));
            var known = await db.SecurityFindings.AsNoTracking().SingleAsync(item => item.Fingerprint == "fp-known", Token);
            Assert.Equal(FindingStatus.Suppressed, known.Status);
        });
    }

    [Fact]
    public async Task ResuppressingAnExpiredFingerprintAppliesToLaterScans() {
        await using var host = await ScanHost.StartAsync(_reports);
        var (adminId, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var artifact = await host.CreateArtifactAsync();
        var now = DateTimeOffset.UtcNow;
        await host.WithDbAsync(async db => {
            db.FindingSuppressions.Add(FindingSuppression.Create(artifact.ProjectId, "fp-x", "Old", adminId, now.AddDays(-10), now.AddDays(-1)));
            await db.SaveChangesAsync(Token);
        });
        using (var suppress = await host.SendAsync(HttpMethod.Post, "/api/v1/projects/circuit-service/finding-suppressions", admin,
            new SuppressFindingRequest("fp-x", "Accepted again", null)))
            Assert.Equal(HttpStatusCode.OK, suppress.StatusCode);

        using var upload = await host.UploadAsync(artifact.Id, admin, Sarif(Result("r", "error", "fp-x")));

        Assert.Equal(1, (await ReadJsonAsync(upload)).GetProperty("summary").GetProperty("suppressed").GetInt32());
    }

    // ---------- helpers ----------

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.Clone();
    }

    private static string Result(string ruleId, string? level, string fingerprint, double? score = null) {
        var result = new Dictionary<string, object> {
            ["ruleId"] = ruleId,
            ["message"] = new { text = ruleId + " found" },
            ["partialFingerprints"] = new { primary = fingerprint },
            ["locations"] = new[] {
                new { physicalLocation = new { artifactLocation = new { uri = "src/app.cs" }, region = new { startLine = 7 } } },
            },
        };
        if (level is not null) result["level"] = level;
        if (score is not null) result["properties"] = new Dictionary<string, string> {
            ["security-severity"] = score.Value.ToString(CultureInfo.InvariantCulture),
        };
        return JsonSerializer.Serialize(result);
    }

    private static byte[] Sarif(params string[] results) => Encoding.UTF8.GetBytes(SarifText("", results));

    private static string SarifText(string padding, string[] results) =>
        $$$"""{"version":"2.1.0","runs":[{"tool":{"driver":{"name":"test"}},"properties":{"padding":"{{{padding}}}"},"results":[{{{string.Join(",", results)}}}]}]}""";

    /// <summary>Builds a valid SARIF document of exactly <paramref name="size"/> bytes.</summary>
    private static byte[] PaddedSarif(int size, params string[] results) {
        var baseLength = Encoding.UTF8.GetByteCount(SarifText("", results));
        var bytes = Encoding.UTF8.GetBytes(SarifText(new string('x', size - baseLength), results));
        Assert.Equal(size, bytes.Length);
        return bytes;
    }

    private static byte[] OversizedReport() => PaddedSarif(UploadScan.MaxBytes + 1, Result("r", "error", "fp"));

    private sealed class ScanHost : IAsyncDisposable {
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "scan-upload-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;
        private Guid _teamId;

        private ScanHost(WebApplication app, SqliteConnection connection) {
            _app = app;
            _connection = connection;
            _client = app.GetTestClient();
            _client.Timeout = TimeSpan.FromMinutes(2);
        }

        public static async Task<ScanHost> StartAsync(string reportsPath, bool mirrorProgramFormLimit = true) {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
                ["Storage:ReportsPath"] = reportsPath,
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddScoped<IAuditWriter, EfAuditWriter>();
            builder.Services.AddJwtAuthentication(builder.Configuration);
            if (mirrorProgramFormLimit) {
                // Same as Program.cs.
                builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = UploadScan.MaxBytes);
            }

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            // Typed lambdas avoid an ASP.NET route-handler analyzer crash on method groups in this test project.
            app.MapPost("/api/v1/artifacts/{id:guid}/scans", (Guid id, HttpRequest request, ClaimsPrincipal principal,
                UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit, TimeProvider time,
                IWebHostEnvironment environment, IConfiguration configuration) =>
                UploadScan.Handle(id, request, principal, users, db, audit, time, environment, configuration))
                .DisableAntiforgery()
                .RequireAuthorization(Permissions.ScansWrite);
            app.MapGet("/api/v1/artifacts/{id:guid}/findings", (Guid id, ClaimsPrincipal principal, UserManager<User> users,
                AegisOpsDbContext db, HttpContext http) => ListFindings.Handle(id, principal, users, db, http))
                .RequireAuthorization(Permissions.FindingsRead);
            app.MapPost("/api/v1/projects/{slug}/finding-suppressions", (string slug, SuppressFindingRequest body,
                ClaimsPrincipal principal, UserManager<User> users, AegisOpsDbContext db, IAuditWriter audit,
                TimeProvider time, HttpContext http) =>
                SuppressFinding.Handle(slug, body, principal, users, db, audit, time, http))
                .RequireAuthorization(Permissions.FindingsSuppress);
            await app.StartAsync(Token);

            var host = new ScanHost(app, connection);
            await host.WithDbAsync(db => db.Database.EnsureCreatedAsync(Token));
            return host;
        }

        public async Task<(Guid Id, string Bearer)> CreateUserAsync(string email, string role) {
            await using var scope = _app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            if (!await roles.RoleExistsAsync(role))
                Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.NewGuid() })).Succeeded);
            var user = User.Create(email, email.Split('@')[0], DateTimeOffset.UtcNow);
            Assert.True((await users.CreateAsync(user)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            return (user.Id, JwtAccessTokenFactory.Create(user, [role], Jwt, DateTimeOffset.UtcNow));
        }

        public Task<Artifact> CreateArtifactAsync(string slug = "circuit-service") => WithDbAsync(async db => {
            var now = DateTimeOffset.UtcNow;
            if (_teamId == Guid.Empty) {
                var team = Team.Create("Platform", "platform", now);
                db.Teams.Add(team);
                _teamId = team.Id;
            }
            var project = Project.Create(_teamId, slug, slug, now);
            var creatorId = await db.Users.Select(user => user.Id).FirstAsync(Token);
            var artifact = Artifact.Create(project.Id, "1.0.0", new string('a', 40), "main", $"ghcr.io/x/{slug}:1.0.0",
                CheckStatus.Passed, CheckStatus.Passed, now, createdById: creatorId);
            db.AddRange(project, artifact);
            await db.SaveChangesAsync(Token);
            return artifact;
        });

        public Task<string> CreateApiKeyAsync(Guid projectId, Guid ownerId, string[] scopes, bool revoked = false) => WithDbAsync(async db => {
            var generated = ApiKeyGenerator.Create();
            var now = DateTimeOffset.UtcNow;
            var key = ApiKey.Create(projectId, "ci", generated.Prefix, generated.Hash, scopes, now.AddDays(30), ownerId, now.AddMinutes(-1));
            if (revoked) key.Revoke(now);
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync(Token);
            return generated.Plaintext;
        });

        public Task<HttpResponseMessage> UploadAsync(Guid artifactId, string? bearer, byte[]? report, string scanner = "semgrep",
            string format = "sarif", string? toolVersion = null) {
            var content = new MultipartFormDataContent {
                { new StringContent(scanner), "scanner" },
                { new StringContent(format), "format" },
            };
            if (toolVersion is not null) content.Add(new StringContent(toolVersion), "toolVersion");
            if (report is not null) {
                var file = new ByteArrayContent(report);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/sarif+json");
                content.Add(file, "report", "report.sarif");
            }
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/artifacts/{artifactId}/scans") { Content = content };
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return _client.SendAsync(request, Token);
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, object? body = null) {
            var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
            return _client.SendAsync(request, Token);
        }

        public async Task WithDbAsync(Func<AegisOpsDbContext, Task> action) {
            await using var scope = _app.Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>());
        }

        public async Task<T> WithDbAsync<T>(Func<AegisOpsDbContext, Task<T>> action) {
            await using var scope = _app.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>());
        }

        public async ValueTask DisposeAsync() {
            _client.Dispose();
            await _app.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// SQLite has no text[] columns: persist API-key scopes and policy tiers as comma-separated text so the
    /// real API-key handler sees the key's scopes.
    /// </summary>
    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Property(key => key.Scopes)
                .HasConversion(new ValueConverter<IReadOnlyList<string>, string>(
                    scopes => string.Join(",", scopes),
                    text => text.Split(',', StringSplitOptions.RemoveEmptyEntries)))
                .HasColumnType("TEXT");
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
        }
    }
}
