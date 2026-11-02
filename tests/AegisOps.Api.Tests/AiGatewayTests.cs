using AegisOps.Infrastructure.Ai;
using AegisOps.Domain.Ai;
using AegisOps.Domain.Policy;
using AegisOps.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using AegisOps.Domain.Identity;
using AegisOps.Domain.Deploy;
using AegisOps.Api.Ai;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AegisOps.Api.Tests;

public sealed class AiGatewayTests {
    [Fact]
    public async Task Chat_endpoint_rejects_principal_without_valid_subject_before_accessing_services() {
        var result = await AiEndpoints.Chat(new AiChatRequest(null, null, []), new ClaimsPrincipal(), null!, null!, null!, new DefaultHttpContext());
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Ollama_client_uses_chat_and_tags_contracts_without_streaming() {
        var requests = new List<HttpRequestMessage>();
        using var client = new HttpClient(new StubHandler(async (request, ct) => {
            requests.Add(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { models = new[] { new { name = "llama3.2:3b" } } }) };
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = JsonContent.Create(new { message = new { content = "safe reply" }, prompt_eval_count = 7, eval_count = 9 })
            };
        })) { BaseAddress = new Uri("http://ollama.test/") };
        var llm = new OllamaLlmClient(client);

        var models = await llm.ListModels(TestContext.Current.CancellationToken);
        var reply = await llm.Chat("llama3.2:3b", [new LlmMessage("user", "hello")], TestContext.Current.CancellationToken);

        Assert.Equal(["llama3.2:3b"], models);
        Assert.Equal("safe reply", reply.Content);
        Assert.Equal(7, reply.PromptTokens);
        Assert.Equal(9, reply.CompletionTokens);
        Assert.EndsWith("/api/tags", requests[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/api/chat", requests[1].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await requests[1].Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData("Redact", 200, "[REDACTED:AWS_ACCESS_KEY]", "Completed")]
    [InlineData("Block", 422, "", "Blocked")]
    public async Task Gateway_enforces_sensitive_policy_and_minimizes_audit(string action, int status, string sent, string recorded) {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("llama3.2:3b"); model.SetEnabled(true);
        var policy = AiPolicy.Create("baseline", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, Enum.Parse<SensitiveDataAction>(action), [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy);
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm();
        var gateway = new AiGateway(fixture.Db, fake, new FakeLimiter(), new RegexSensitiveDataDetector(), TimeProvider.System);
        const string secret = "AKIA1234567890123456";
        var result = await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hello " + secret)], "correlation", TestContext.Current.CancellationToken);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(recorded == "Completed" ? 1 : 0, fake.Calls);
        if (fake.Calls > 0) Assert.Contains(sent, fake.LastPrompt);
        var row = await fixture.Db.AiRequests.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(recorded, row.Status.ToString());
        Assert.DoesNotContain(secret, row.PromptPreview);
        Assert.Null(row.PromptContent);
        Assert.Equal(64, row.PromptHash.Length);
        Assert.Single(await fixture.Db.AuditEvents.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Gateway_logs_rate_limit_and_disallowed_model_without_calling_llm() {
        await using var fixture = new GatewayFixture();
        var model = AiModel.Create("llama3.2:3b"); model.SetEnabled(true);
        var policy = AiPolicy.Create("baseline", PolicyScope.Global, null, fixture.UserId, DateTimeOffset.UtcNow);
        policy.Configure([model.Id], 20, 500, 16000, SensitiveDataAction.Redact, [], false, false, true, fixture.UserId, DateTimeOffset.UtcNow);
        fixture.Db.AiModels.Add(model); fixture.Db.AiPolicies.Add(policy); await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fake = new FakeLlm(); var limiter = new FakeLimiter { Allow = false };
        var gateway = new AiGateway(fixture.Db, fake, limiter, new RegexSensitiveDataDetector(), TimeProvider.System);
        Assert.Equal(429, (await gateway.Chat(fixture.UserId, null, model.Name, [new LlmMessage("user", "hi")], "a", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(403, (await gateway.Chat(fixture.UserId, null, "other", [new LlmMessage("user", "hi")], "b", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(2, await fixture.Db.AiRequests.CountAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FakeLlm : ILlmClient {
        public int Calls;
        public string LastPrompt = "";
        public Task<LlmReply> Chat(string model, IReadOnlyList<LlmMessage> messages, CancellationToken ct) {
            Calls++; LastPrompt = messages[0].Content;
            return Task.FromResult(new LlmReply("response", 2, 3));
        }
        public Task<IReadOnlyList<string>> ListModels(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["llama3.2:3b"]);
    }
    private sealed class FakeLimiter : IAiRateLimiter {
        public bool Allow = true;
        public Task<RateLimitDecision> Check(Guid userId, int minute, int day, CancellationToken ct) =>
            Task.FromResult(new RateLimitDecision(Allow, Allow ? 19 : 0, Allow ? 499 : 0));
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class GatewayFixture : IAsyncDisposable {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public AegisOpsDbContext Db { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public GatewayFixture() {
            connection.Open();
            Db = new AegisOpsDbContext(new DbContextOptionsBuilder<AegisOpsDbContext>().UseSqlite(connection)
                .UseSnakeCaseNamingConvention().ReplaceService<IModelCustomizer, SqliteCustomizer>().Options);
            Db.Database.EnsureCreated();
            Db.Users.Add(User.Create($"ai-{UserId:N}@example.test", "AI tester", DateTimeOffset.UtcNow));
            // Use the generated Identity user key for FK integrity.
            UserId = Db.Users.Local.Single().Id;
            Db.SaveChanges();
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
    private sealed class SqliteCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies) {
        public override void Customize(ModelBuilder modelBuilder, DbContext context) {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<AegisOps.Domain.Identity.ApiKey>().Ignore(key => key.Scopes);
            modelBuilder.Entity<AegisOps.Domain.Policy.Policy>().Ignore(policy => policy.AppliesToTiers);
            modelBuilder.Entity<Deployment>().Property(item => item.RequestedAt)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
            modelBuilder.Entity<DeploymentEvent>().Property(item => item.Timestamp)
                .HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter());
        }
    }

    [Theory]
    [InlineData("AKIA1234567890123456", "AWS_ACCESS_KEY")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "GITHUB_TOKEN")]
    [InlineData("my id 900101-14-1234", "MY_NRIC")]
    [InlineData("contact +60123456789", "PHONE_MY")]
    [InlineData("server 192.168.1.12", "IPV4_PRIVATE")]
    [InlineData("4111 1111 1111 1111", "CREDIT_CARD")]
    public void Detector_redacts_sensitive_spans(string text, string type) {
        var result = new RegexSensitiveDataDetector().Inspect(text, redactPii: true);
        Assert.Contains(type, result.Types);
        Assert.DoesNotContain(text, result.Redacted);
        Assert.Contains("[REDACTED:" + type + "]", result.Redacted);
    }
}
