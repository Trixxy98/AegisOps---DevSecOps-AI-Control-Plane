using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AegisOps.Domain.Deploy;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Organization;
using AegisOps.Domain.Policy;
using AegisOps.Domain.Security;
using AegisOps.Infrastructure.Deploy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using ProjectEnvironment = AegisOps.Domain.Organization.Environment;

namespace AegisOps.Api.Tests;

public sealed class TeamWebhookTests {
    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://10.2.3.4/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com:8443/")]
    [InlineData("https://example.com/#fragment")]
    public void Unsafe_urls_are_rejected(string url) => Assert.False(TeamWebhookSender.TryValidateUrl(url, out _));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("100.100.100.200")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.2")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Non_public_addresses_are_rejected(string address) =>
        Assert.False(TeamWebhookSender.IsPublicIpv4(IPAddress.Parse(address)));

    [Fact]
    public void Public_https_is_accepted_and_jobs_retry_with_backoff() {
        Assert.True(TeamWebhookSender.TryValidateUrl("https://example.com/notifications", out _));
        Assert.True(TeamWebhookSender.IsPublicIpv4(IPAddress.Parse("8.8.8.8")));
        var now = DateTimeOffset.UtcNow;
        var job = Job.Enqueue(JobType.SendNotification, "{\"eventId\":\"" + Guid.NewGuid() + "\"}", now, "test");
        job.MarkRunning(now, "worker");
        job.Fail("Webhook delivery failed.", now);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(now.AddSeconds(2), job.ScheduledAt);
        for (var i = 1; i < 5; i++) {
            job.MarkRunning(job.ScheduledAt, "worker");
            job.Fail("Webhook delivery failed.", job.ScheduledAt);
        }
        Assert.Equal(JobStatus.DeadLettered, job.Status);
    }

    [Fact]
    public void Signature_covers_timestamp_and_exact_body() {
        var key = Enumerable.Repeat((byte)42, 32).ToArray();
        var expected = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("123.{\"id\":1}"))).ToLowerInvariant();
        Assert.Equal(expected, TeamWebhookSender.Sign(key, "123", "{\"id\":1}"));
        Assert.NotEqual(expected, TeamWebhookSender.Sign(key, "124", "{\"id\":1}"));
        Assert.NotEqual(expected, TeamWebhookSender.Sign(key, "123", "{\"id\":2}"));
    }

    [Fact]
    public async Task Send_posts_occurrence_payload_and_returns_only_generic_error_for_non_success_response() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>()
            .Options;
        await using var db = new AegisOpsDbContext(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var team = Team.Create("Webhook Team", "webhook-team", now);
        var project = Project.Create(team.Id, "Webhook Project", "webhook-project", now);
        var environment = ProjectEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 0);
        var user = User.Create("webhook@example.test", "Webhook Test", now);
        var artifact = Artifact.Create(project.Id, "1.2.3", new string('a', 40), "main", "example/image:1.2.3",
            CheckStatus.Passed, CheckStatus.Passed, now, createdById: user.Id);
        var deployment = Deployment.Request(project.Id, environment.Id, artifact.Id, now, "webhook-test", user.Id, null, null);
        db.AddRange(team, project, environment, user, artifact, deployment);
        await db.SaveChangesAsync(cancellationToken);

        const string eventMessage = "private deployment note that must not leave the worker";
        var deploymentEvent = DeploymentEvent.Create(deployment.Id, 1, now, DeploymentEventType.Failed, eventMessage);
        db.DeploymentEvents.Add(deploymentEvent);
        await db.SaveChangesAsync(cancellationToken);
        var persistedEvent = await db.DeploymentEvents.AsNoTracking().SingleAsync(item => item.Id == deploymentEvent.Id, cancellationToken);

        var key = Enumerable.Repeat((byte)42, 32).ToArray();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            [$"Webhooks:Teams:{team.Id:D}:Url"] = "https://example.com/notifications",
            [$"Webhooks:Teams:{team.Id:D}:Secret"] = Convert.ToBase64String(key),
        }).Build();
        await using var receiver = new LocalHttpsReceiver([HttpStatusCode.Accepted, HttpStatusCode.InternalServerError], "private response detail");
        var receivedRequests = receiver.ReceiveRequestsAsync(2, cancellationToken);
        await TeamWebhookSender.SendAsync(db, config, deploymentEvent.Id, receiver.CreateClientHandler(), cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TeamWebhookSender.SendAsync(db, config, deploymentEvent.Id, receiver.CreateClientHandler(), cancellationToken));
        Assert.Equal("Webhook delivery failed.", exception.Message);
        Assert.DoesNotContain("private response detail", exception.Message, StringComparison.Ordinal);

        var requests = await receivedRequests;
        Assert.Equal(2, requests.Count);
        var request = requests[0];
        Assert.Equal("POST", request.Method);
        Assert.Equal("/notifications", request.Path);
        Assert.Equal("application/json", request.Headers["Content-Type"].Split(';')[0]);
        Assert.Equal(deploymentEvent.Id.ToString("D"), request.Headers["X-AegisOps-Event-Id"]);
        var signatureTimestamp = request.Headers["X-AegisOps-Timestamp"];
        var expectedSignature = TeamWebhookSender.Sign(key, signatureTimestamp, request.Body);
        Assert.Equal("sha256=" + expectedSignature, request.Headers["X-AegisOps-Signature"]);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(deploymentEvent.Id, body.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Failed", body.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(persistedEvent.Timestamp, body.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(deployment.Id, body.RootElement.GetProperty("deploymentId").GetGuid());
        Assert.Equal(project.Id, body.RootElement.GetProperty("projectId").GetGuid());
        Assert.Equal(environment.Id, body.RootElement.GetProperty("environmentId").GetGuid());
        Assert.Equal(artifact.Id, body.RootElement.GetProperty("artifactId").GetGuid());
        Assert.Equal(1, body.RootElement.GetProperty("sequence").GetInt32());
        Assert.DoesNotContain(eventMessage, request.Body, StringComparison.Ordinal);
        Assert.Equal("private response detail", requests[1].ResponseBody);
    }

    [Fact]
    public async Task Send_prefers_encrypted_database_settings_to_legacy_worker_configuration() {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AegisOpsDbContext>()
            .UseSqlite(connection).UseSnakeCaseNamingConvention()
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>().Options;
        await using var db = new AegisOpsDbContext(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var team = Team.Create("Webhook Team", "webhook-team", now);
        var project = Project.Create(team.Id, "Webhook Project", "webhook-project", now);
        var environment = ProjectEnvironment.Create(project.Id, "Staging", EnvironmentTier.Staging, 0);
        var user = User.Create("webhook@example.test", "Webhook Test", now);
        var artifact = Artifact.Create(project.Id, "1.2.3", new string('a', 40), "main", "example/image:1.2.3",
            CheckStatus.Passed, CheckStatus.Passed, now, createdById: user.Id);
        var deployment = Deployment.Request(project.Id, environment.Id, artifact.Id, now, "webhook-test", user.Id, null, null);
        var deploymentEvent = DeploymentEvent.Create(deployment.Id, 1, now, DeploymentEventType.Succeeded, "done");
        db.AddRange(team, project, environment, user, artifact, deployment, deploymentEvent);

        var signingKey = Enumerable.Repeat((byte)17, 32).ToArray();
        var encryptionKey = Enumerable.Repeat((byte)29, 32).ToArray();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["WebhookSecrets:Enabled"] = "true",
            ["WebhookSecrets:CurrentKeyId"] = "primary",
            ["WebhookSecrets:Keys:primary"] = Convert.ToBase64String(encryptionKey),
            [$"Webhooks:Teams:{team.Id:D}:Url"] = "https://example.com/legacy",
            [$"Webhooks:Teams:{team.Id:D}:Secret"] = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()),
        }).Build();
        var protector = new TeamWebhookSecretProtector(configuration);
        db.TeamWebhookSettings.Add(TeamWebhookSettings.Create(team.Id, "https://example.com/database", protector.Protect(team.Id, signingKey), now));
        await db.SaveChangesAsync(cancellationToken);

        await using var receiver = new LocalHttpsReceiver([HttpStatusCode.Accepted], string.Empty);
        var receivedRequests = receiver.ReceiveRequestsAsync(1, cancellationToken);
        await TeamWebhookSender.SendAsync(db, configuration, deploymentEvent.Id, receiver.CreateClientHandler(), cancellationToken);

        var request = Assert.Single(await receivedRequests);
        Assert.Equal("/database", request.Path);
        using var body = JsonDocument.Parse(request.Body);
        var timestamp = request.Headers["X-AegisOps-Timestamp"];
        Assert.Equal("sha256=" + TeamWebhookSender.Sign(signingKey, timestamp, request.Body), request.Headers["X-AegisOps-Signature"]);
        Assert.Equal(deploymentEvent.Id, body.RootElement.GetProperty("id").GetGuid());
    }

    private sealed class LocalHttpsReceiver(HttpStatusCode[] statuses, string failureBody) : IAsyncDisposable {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 certificate = CreateCertificate();

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public async Task<IReadOnlyList<ReceivedWebhook>> ReceiveRequestsAsync(int count, CancellationToken cancellationToken) {
            listener.Start();
            var received = new List<ReceivedWebhook>(count);
            for (var index = 0; index < count; index++) {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, cancellationToken);
                var request = await ReadRequestAsync(ssl, cancellationToken);
                var responseBody = statuses[index] == HttpStatusCode.InternalServerError ? failureBody : string.Empty;
                received.Add(request with { ResponseBody = responseBody });
                var body = Encoding.UTF8.GetBytes(responseBody);
                var phrase = statuses[index] == HttpStatusCode.Accepted ? "Accepted" : "Internal Server Error";
                var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)statuses[index]} {phrase}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(headers, cancellationToken);
                await ssl.WriteAsync(body, cancellationToken);
                await ssl.FlushAsync(cancellationToken);
            }
            return received;
        }

        public SocketsHttpHandler CreateClientHandler() => new() {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) => {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                } catch {
                    socket.Dispose();
                    throw;
                }
            },
            SslOptions = {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null && errors == SslPolicyErrors.RemoteCertificateChainErrors &&
                    string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        this.certificate.GetCertHashString(HashAlgorithmName.SHA256), StringComparison.OrdinalIgnoreCase),
            },
        };

        private static async Task<ReceivedWebhook> ReadRequestAsync(Stream stream, CancellationToken cancellationToken) {
            var headerBytes = new List<byte>();
            var buffer = new byte[1];
            while (headerBytes.Count < 32_768) {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) throw new IOException("Receiver ended before request headers completed.");
                headerBytes.Add(buffer[0]);
                var length = headerBytes.Count;
                if (length >= 4 && headerBytes[length - 4] == 13 && headerBytes[length - 3] == 10 &&
                    headerBytes[length - 2] == 13 && headerBytes[length - 1] == 10) break;
            }

            if (headerBytes.Count >= 32_768) throw new IOException("Request headers exceeded the test receiver limit.");
            var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1).Select(line => line.Split(':', 2))
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var bodyBytes = new byte[int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture)];
            await stream.ReadExactlyAsync(bodyBytes, cancellationToken);
            return new ReceivedWebhook(requestLine[0], requestLine[1], headers,
                Encoding.UTF8.GetString(bodyBytes), string.Empty);
        }

        private static X509Certificate2 CreateCertificate() {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("example.com");
            request.CertificateExtensions.Add(names.Build());
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public ValueTask DisposeAsync() {
            listener.Stop();
            certificate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ReceivedWebhook(string Method, string Path, Dictionary<string, string> Headers, string Body, string ResponseBody);

    private sealed class SqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }
}
