using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AegisOps.Api.Identity;
using AegisOps.Api.Organization;
using AegisOps.Application.Authorization;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Identity;
using AegisOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
/// GET /api/v1/projects/{slug}/findings (audit check P3-06, backing the findings explorer UI): real JWT and
/// findings:read authorization, docs/09 team scoping (Admin/Security see all, members see their team, others 404),
/// severity/scanner/status/artifact filters, severity ordering, pagination and suppression details.
/// </summary>
public sealed class ProjectFindingsEndpointTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Path = "/api/v1/projects/circuit-service/findings";

    [Fact]
    public async Task ListsProjectFindingsAcrossArtifactsWithScannerArtifactAndSuppressionDetails() {
        await using var host = await FindingsHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var seed = await host.SeedAsync();

        using var response = await host.GetAsync(Path, admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal((1, 50, 5), (body.GetProperty("page").GetInt32(), body.GetProperty("pageSize").GetInt32(), body.GetProperty("total").GetInt32()));
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        // Severity rank, not alphabetical: Critical, High, Medium, Low, Unknown.
        Assert.Equal(["Critical", "High", "Medium", "Low", "Unknown"], items.Select(item => item.GetProperty("severity").GetString()!));
        Assert.DoesNotContain(items, item => item.GetProperty("fingerprint").GetString() == "fp-other-project");

        var critical = items[0];
        Assert.Equal(seed.OldArtifactId, critical.GetProperty("artifactId").GetGuid());
        Assert.Equal("1.0.0", critical.GetProperty("artifactVersion").GetString());
        Assert.Equal("Trivy", critical.GetProperty("scanner").GetString());
        Assert.Equal("CVE-2024-0001", critical.GetProperty("cve").GetString());
        Assert.Equal("openssl", critical.GetProperty("packageName").GetString());
        Assert.Equal("3.0.1", critical.GetProperty("installedVersion").GetString());
        Assert.Equal("3.0.9", critical.GetProperty("fixedVersion").GetString());
        Assert.Equal("https://example.test/CVE-2024-0001", critical.GetProperty("helpUri").GetString());
        Assert.Equal("Critical issue description", critical.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, critical.GetProperty("suppression").ValueKind);

        var suppressed = items.Single(item => item.GetProperty("fingerprint").GetString() == "fp-low");
        Assert.Equal("Suppressed", suppressed.GetProperty("status").GetString());
        Assert.Equal("Accepted risk", suppressed.GetProperty("suppressionReason").GetString());
        var suppression = suppressed.GetProperty("suppression");
        Assert.Equal("Accepted risk", suppression.GetProperty("reason").GetString());
        Assert.Equal(seed.SuppressionExpiresAt, suppression.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("src/app.cs", suppressed.GetProperty("filePath").GetString());
        Assert.Equal(7, suppressed.GetProperty("startLine").GetInt32());
    }

    [Fact]
    public async Task FiltersBySeverityScannerStatusAndArtifact() {
        await using var host = await FindingsHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var seed = await host.SeedAsync();

        async Task<string[]> Fingerprints(string query) {
            using var response = await host.GetAsync(Path + query, admin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await ReadJsonAsync(response);
            var fingerprints = body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("fingerprint").GetString()!).ToArray();
            Assert.Equal(fingerprints.Length, body.GetProperty("total").GetInt32());
            return fingerprints;
        }

        Assert.Equal(["fp-high"], await Fingerprints("?severity=high"));
        Assert.Equal(["fp-critical", "fp-low"], await Fingerprints("?scanner=Trivy"));
        Assert.Equal(["fp-low"], await Fingerprints("?status=Suppressed"));
        Assert.Equal(["fp-critical", "fp-high", "fp-medium", "fp-unknown"], await Fingerprints("?status=Open"));
        Assert.Equal(["fp-high", "fp-medium", "fp-unknown"], await Fingerprints($"?artifactId={seed.NewArtifactId}"));
        Assert.Equal(["fp-medium"], await Fingerprints($"?artifactId={seed.NewArtifactId}&scanner=Semgrep&status=Open&severity=Medium"));
        Assert.Empty(await Fingerprints($"?artifactId={seed.OtherProjectArtifactId}"));
        Assert.Empty(await Fingerprints($"?artifactId={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task PaginatesAndRejectsInvalidPaginationAndFilters() {
        await using var host = await FindingsHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        await host.SeedAsync();

        using (var second = await host.GetAsync(Path + "?page=2&pageSize=2", admin)) {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var body = await ReadJsonAsync(second);
            Assert.Equal(5, body.GetProperty("total").GetInt32());
            Assert.Equal(["Medium", "Low"], body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("severity").GetString()!));
        }

        foreach (var query in new[] {
                     "?page=0", "?pageSize=0", "?pageSize=101", "?severity=Severe", "?severity=5", "?scanner=Snyk",
                     "?status=Closed", "?status=Open,Suppressed",
                 }) {
            using var response = await host.GetAsync(Path + query, admin);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{query} returned {response.StatusCode}");
        }
    }

    [Fact]
    public async Task IsTeamScopedForMembersAndOpenToAdminAndSecurity() {
        await using var host = await FindingsHost.StartAsync();
        var (_, admin) = await host.CreateUserAsync("admin@example.test", "Admin");
        var (_, security) = await host.CreateUserAsync("security@example.test", "Security");
        var (memberId, member) = await host.CreateUserAsync("member@example.test", "Developer");
        var (approverId, approver) = await host.CreateUserAsync("approver@example.test", "Approver");
        var (_, outsider) = await host.CreateUserAsync("outsider@example.test", "Developer");
        var seed = await host.SeedAsync();
        await host.AddMemberAsync(seed.TeamId, memberId);
        await host.AddMemberAsync(seed.TeamId, approverId);

        async Task<HttpStatusCode> Status(string? bearer, string path = Path) {
            using var response = await host.GetAsync(path, bearer);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Status(admin));
        Assert.Equal(HttpStatusCode.OK, await Status(security));
        Assert.Equal(HttpStatusCode.OK, await Status(member));
        Assert.Equal(HttpStatusCode.OK, await Status(approver));
        // A non-member gets 404 so the project is not revealed; same as an unknown project.
        Assert.Equal(HttpStatusCode.NotFound, await Status(outsider));
        Assert.Equal(HttpStatusCode.NotFound, await Status(admin, "/api/v1/projects/nope/findings"));
        // Members of the other team cannot read this project's findings either.
        Assert.Equal(HttpStatusCode.NotFound, await Status(member, "/api/v1/projects/other-service/findings"));
        Assert.Equal(HttpStatusCode.OK, await Status(security, "/api/v1/projects/other-service/findings"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(null));
        // An unknown role holds no findings:read permission.
        var (_, auditor) = await host.CreateUserAsync("auditor@example.test", "Auditor");
        Assert.Equal(HttpStatusCode.Forbidden, await Status(auditor));
    }

    [Fact]
    public async Task ArtifactFindingsAreTeamScopedAndOrderedBySeverity() {
        await using var host = await FindingsHost.StartAsync();
        var (_, security) = await host.CreateUserAsync("security@example.test", "Security");
        var (memberId, member) = await host.CreateUserAsync("member@example.test", "Developer");
        var (_, outsider) = await host.CreateUserAsync("outsider@example.test", "Developer");
        var seed = await host.SeedAsync();
        await host.AddMemberAsync(seed.TeamId, memberId);
        string ArtifactPath(Guid id) => $"/api/v1/artifacts/{id}/findings";

        using (var own = await host.GetAsync(ArtifactPath(seed.NewArtifactId), member)) {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var ranks = (await ReadJsonAsync(own)).EnumerateArray()
                .Select(item => (int)Enum.Parse<Severity>(item.GetProperty("severity").GetString()!)).ToArray();
            Assert.NotEmpty(ranks);
            Assert.Equal(ranks.OrderByDescending(rank => rank), ranks);
        }
        // Regression: this route previously returned any team's findings to any reader.
        using (var foreign = await host.GetAsync(ArtifactPath(seed.OtherProjectArtifactId), member))
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using (var stranger = await host.GetAsync(ArtifactPath(seed.NewArtifactId), outsider))
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        using (var unknown = await host.GetAsync(ArtifactPath(Guid.NewGuid()), security))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var crossTeam = await host.GetAsync(ArtifactPath(seed.OtherProjectArtifactId), security))
            Assert.Equal(HttpStatusCode.OK, crossTeam.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.Clone();
    }

    private sealed record Seed(Guid TeamId, Guid OldArtifactId, Guid NewArtifactId, Guid OtherProjectArtifactId, DateTimeOffset SuppressionExpiresAt);

    private sealed class FindingsHost : IAsyncDisposable {
        private static readonly JwtOptions Jwt = new() {
            Issuer = "aegisops-tests",
            Audience = "aegisops-tests",
            SigningKey = "project-findings-tests-signing-key-0123456789",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7,
        };

        private readonly WebApplication _app;
        private readonly SqliteConnection _connection;
        private readonly HttpClient _client;

        private FindingsHost(WebApplication app, SqliteConnection connection) {
            _app = app;
            _connection = connection;
            _client = app.GetTestClient();
        }

        public static async Task<FindingsHost> StartAsync() {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync(Token);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["Jwt:Issuer"] = Jwt.Issuer,
                ["Jwt:Audience"] = Jwt.Audience,
                ["Jwt:SigningKey"] = Jwt.SigningKey,
                ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
            });
            builder.Services.AddDbContext<AegisOpsDbContext>(options => options.UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteModelCustomizer>());
            builder.Services.AddIdentityCore<User>(IdentityStoreExtensions.ConfigureIdentity).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AegisOpsDbContext>();
            builder.Services.AddJwtAuthentication(builder.Configuration);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            // Same route and policy as Program.cs; a typed lambda avoids the route-handler analyzer crash on method groups.
            app.MapGet("/api/v1/projects/{slug}/findings", (string slug, ClaimsPrincipal principal, UserManager<User> users,
                    AegisOpsDbContext db, HttpContext http, string? severity, string? scanner, string? status, Guid? artifactId,
                    int? page, int? pageSize) =>
                    ListProjectFindings.Handle(slug, principal, users, db, http, severity, scanner, status, artifactId,
                        page ?? 1, pageSize ?? 50))
                .RequireAuthorization(Permissions.FindingsRead);
            app.MapGet("/api/v1/artifacts/{id:guid}/findings", (Guid id, ClaimsPrincipal principal, UserManager<User> users,
                    AegisOpsDbContext db, HttpContext http) => ListFindings.Handle(id, principal, users, db, http))
                .RequireAuthorization(Permissions.FindingsRead);
            await app.StartAsync(Token);

            var host = new FindingsHost(app, connection);
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

        public Task AddMemberAsync(Guid teamId, Guid userId) => WithDbAsync(async db => {
            db.TeamMembers.Add(TeamMember.Create(teamId, userId, TeamRole.Member, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Token);
        });

        /// <summary>
        /// circuit-service (team Platform): artifact 1.0.0 with a Trivy scan (Critical open, Low suppressed with an
        /// active suppression) and artifact 1.1.0 with a Semgrep scan (High, Medium, Unknown open).
        /// other-service (team Payments): one Critical finding that must never appear in circuit-service results.
        /// </summary>
        public Task<Seed> SeedAsync() => WithDbAsync(async db => {
            var now = DateTimeOffset.UtcNow;
            var creatorId = await db.Users.Select(user => user.Id).FirstAsync(Token);
            var team = Team.Create("Platform", "platform", now);
            var otherTeam = Team.Create("Payments", "payments", now);
            var project = Project.Create(team.Id, "circuit-service", "circuit-service", now);
            var otherProject = Project.Create(otherTeam.Id, "other-service", "other-service", now);
            var oldArtifact = NewArtifact(project.Id, "1.0.0", now, creatorId);
            var newArtifact = NewArtifact(project.Id, "1.1.0", now, creatorId);
            var otherArtifact = NewArtifact(otherProject.Id, "9.0.0", now, creatorId);
            var trivy = NewScan(oldArtifact.Id, ScannerType.Trivy, now);
            var semgrep = NewScan(newArtifact.Id, ScannerType.Semgrep, now);
            var otherScan = NewScan(otherArtifact.Id, ScannerType.Trivy, now);
            var expiresAt = new DateTimeOffset(2031, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var suppression = FindingSuppression.Create(project.Id, "fp-low", "Accepted risk", creatorId, now, expiresAt);

            db.AddRange(team, otherTeam, project, otherProject, oldArtifact, newArtifact, otherArtifact, trivy, semgrep, otherScan, suppression);
            db.SecurityFindings.AddRange(
                SecurityFinding.Create(trivy.Id, oldArtifact.Id, Severity.Critical, "CVE-2024-0001", "OpenSSL overflow", "fp-critical",
                    description: "Critical issue description", packageName: "openssl", installedVersion: "3.0.1",
                    fixedVersion: "3.0.9", cve: "CVE-2024-0001", helpUri: "https://example.test/CVE-2024-0001"),
                SecurityFinding.Create(trivy.Id, oldArtifact.Id, Severity.Low, "trivy.low", "Low issue", "fp-low",
                    filePath: "src/app.cs", startLine: 7, status: FindingStatus.Suppressed, suppressedById: creatorId,
                    suppressionReason: "Accepted risk"),
                SecurityFinding.Create(semgrep.Id, newArtifact.Id, Severity.High, "semgrep.sqli", "SQL injection", "fp-high"),
                SecurityFinding.Create(semgrep.Id, newArtifact.Id, Severity.Medium, "semgrep.weak", "Weak hash", "fp-medium"),
                SecurityFinding.Create(semgrep.Id, newArtifact.Id, Severity.Unknown, "semgrep.unknown", "Unknown issue", "fp-unknown"),
                SecurityFinding.Create(otherScan.Id, otherArtifact.Id, Severity.Critical, "CVE-2024-9999", "Other project", "fp-other-project"));
            await db.SaveChangesAsync(Token);
            return new Seed(team.Id, oldArtifact.Id, newArtifact.Id, otherArtifact.Id, expiresAt);
        });

        private static Artifact NewArtifact(Guid projectId, string version, DateTimeOffset now, Guid creatorId) =>
            Artifact.Create(projectId, version, new string('a', 40), "main", $"ghcr.io/x/app:{version}",
                CheckStatus.Passed, CheckStatus.Passed, now, createdById: creatorId);

        private static SecurityScan NewScan(Guid artifactId, ScannerType scanner, DateTimeOffset now) =>
            SecurityScan.Begin(artifactId, scanner, ScanSource.CiUploaded, ReportFormat.Sarif, now, null, "/reports/test.sarif", parseInline: true);

        public Task<HttpResponseMessage> GetAsync(string path, string? bearer) {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
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

    /// <summary>SQLite has no text[] columns; same customization as the scan-upload tests.</summary>
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
