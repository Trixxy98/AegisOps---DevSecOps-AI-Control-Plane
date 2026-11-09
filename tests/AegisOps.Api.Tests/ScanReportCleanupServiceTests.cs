using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Persistence;
using AegisOps.Infrastructure.Scanning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AegisOps.Api.Tests;

public sealed class ScanReportCleanupServiceTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aegisops-report-cleanup-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Cleanup_expires_old_finished_reports_and_deletes_only_unreferenced_old_files() {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        await connection.OpenAsync(ct);
        await using var provider = Services(connection, retentionDays: null);
        var artifactId = Guid.NewGuid();
        var oldFile = Report(artifactId, "Semgrep-old.sarif", now.AddDays(-100));
        var recentFile = Report(artifactId, "Trivy-recent.sarif", now.AddDays(-10));
        var queuedFile = Report(artifactId, "Gitleaks-queued.sarif", now.AddDays(-100));
        // Referenced through a differently spelled root: must still count as referenced.
        var otherArtifactId = Guid.NewGuid();
        var otherRootFile = Report(otherArtifactId, "Semgrep-other-root.sarif", now.AddDays(-5));
        var orphanOld = Report(artifactId, "Semgrep-replaced.sarif", now.AddHours(-2));
        var orphanFresh = Report(artifactId, "Semgrep-uploading.sarif", now.AddMinutes(-5));
        var notSarif = Report(artifactId, "notes.txt", now.AddDays(-100));
        var foreignDir = Path.Combine(_root, "not-an-artifact");
        Directory.CreateDirectory(foreignDir);
        var foreignFile = Path.Combine(foreignDir, "keep.sarif");
        await File.WriteAllTextAsync(foreignFile, "{}", ct);
        File.SetLastWriteTimeUtc(foreignFile, now.AddDays(-100).UtcDateTime);

        Guid oldScanId, recentScanId, queuedScanId;
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            var old = Finished(artifactId, ScannerType.Semgrep, oldFile, now.AddDays(-100));
            var recent = Finished(artifactId, ScannerType.Trivy, recentFile, now.AddDays(-10));
            var queued = SecurityScan.Begin(artifactId, ScannerType.Gitleaks, ScanSource.CiUploaded, ReportFormat.Sarif,
                now.AddDays(-100), null, queuedFile, parseInline: false);
            var otherRoot = Finished(otherArtifactId, ScannerType.Semgrep,
                Path.Combine("/elsewhere/reports", otherArtifactId.ToString("N"), "Semgrep-other-root.sarif"), now.AddDays(-5));
            db.SecurityScans.AddRange(old, recent, queued, otherRoot);
            await db.SaveChangesAsync(ct);
            (oldScanId, recentScanId, queuedScanId) = (old.Id, recent.Id, queued.Id);
        }

        var (expired, deleted) = await provider.GetRequiredService<ScanReportCleanupService>().CleanupAsync(ct);

        Assert.Equal(1, expired);
        Assert.Equal(2, deleted);
        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(orphanOld));
        Assert.True(File.Exists(recentFile));
        Assert.True(File.Exists(queuedFile));
        Assert.True(File.Exists(otherRootFile));
        Assert.True(File.Exists(orphanFresh));
        Assert.True(File.Exists(notSarif));
        Assert.True(File.Exists(foreignFile));
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            var scans = await db.SecurityScans.AsNoTracking().ToDictionaryAsync(scan => scan.Id, ct);
            // The scan and its summary stay; only the raw report reference is removed.
            Assert.Null(scans[oldScanId].RawReportPath);
            Assert.Equal(ScanStatus.Completed, scans[oldScanId].Status);
            Assert.Equal(recentFile, scans[recentScanId].RawReportPath);
            Assert.Equal(queuedFile, scans[queuedScanId].RawReportPath);
        }
        Assert.Equal((0, 0), await provider.GetRequiredService<ScanReportCleanupService>().CleanupAsync(ct));
    }

    [Fact]
    public async Task Retention_days_are_configurable() {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=False");
        await connection.OpenAsync(ct);
        await using var provider = Services(connection, retentionDays: 7);
        var artifactId = Guid.NewGuid();
        var file = Report(artifactId, "Trivy-eight-days.sarif", now.AddDays(-8));
        await using (var scope = provider.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AegisOpsDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.SecurityScans.Add(Finished(artifactId, ScannerType.Trivy, file, now.AddDays(-8)));
            await db.SaveChangesAsync(ct);
        }

        Assert.Equal((1, 1), await provider.GetRequiredService<ScanReportCleanupService>().CleanupAsync(ct));
        Assert.False(File.Exists(file));
    }

    private string Report(Guid artifactId, string name, DateTimeOffset written) {
        var directory = Path.Combine(_root, artifactId.ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, """{"runs":[]}""");
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
        return path;
    }

    private static SecurityScan Finished(Guid artifactId, ScannerType scanner, string path, DateTimeOffset at) {
        var scan = SecurityScan.Begin(artifactId, scanner, ScanSource.CiUploaded, ReportFormat.Sarif, at, null, path, parseInline: true);
        scan.Complete(ScanSummary.Empty(), at);
        return scan;
    }

    private ServiceProvider Services(SqliteConnection connection, int? retentionDays) {
        var settings = new Dictionary<string, string?> { ["Storage:ReportsPath"] = _root };
        if (retentionDays is not null) settings["Storage:ReportRetentionDays"] = retentionDays.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(_root));
        services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteCustomizer>());
        services.AddSingleton<ScanReportCleanupService>();
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string root) : IHostEnvironment {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AegisOps.Worker";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<SecurityScan>().Property(scan => scan.CompletedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        }
    }
}
