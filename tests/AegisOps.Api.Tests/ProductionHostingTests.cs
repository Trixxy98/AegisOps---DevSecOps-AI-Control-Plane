using System.Net;
using System.Text.Json;
using AegisOps.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AegisOps.Api.Tests;

/// <summary>
/// Roadmap v1.1 items 1, 3 and 5: production startup refusals, opt-in forwarded headers from known proxies only,
/// and unhandled exceptions as Problem Details with a traceId.
/// </summary>
public sealed class ProductionHostingTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string GoodKey = "production-hosting-tests-signing-key-0123456789";

    // ---------- Startup refusals ----------

    [Fact]
    public void ProductionRefusesThePlaceholderSigningKeyAndTheDevelopmentDatabasePassword() {
        var problems = ProductionSafety.Problems(Config(new() {
            ["Jwt:SigningKey"] = ProductionSafety.ExampleSigningKey,
            ["ConnectionStrings:Postgres"] = "Host=postgres;Database=aegisops;Username=aegisops;Password=aegisops_dev",
        }), new Env("Production"));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, item => item.Contains(".env.example placeholder", StringComparison.Ordinal));
        Assert.Contains(problems, item => item.Contains("Development password", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, item => item.Contains(ProductionSafety.ExampleSigningKey, StringComparison.Ordinal));
        var error = Assert.Throws<InvalidOperationException>(() => ProductionSafety.ThrowIfUnsafe(Config(new() {
            ["Jwt:SigningKey"] = ProductionSafety.ExampleSigningKey,
        }), new Env("Production")));
        Assert.Contains("Refusing to start in the Production environment", error.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings:Postgres is missing.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptsRealSecretsAndDevelopmentIsNotChecked() {
        var safe = Config(new() {
            ["Jwt:SigningKey"] = GoodKey,
            ["ConnectionStrings:Postgres"] = "Host=postgres;Database=aegisops;Username=aegisops;Password=generated-elsewhere",
        });
        Assert.Empty(ProductionSafety.Problems(safe, new Env("Production")));
        Assert.Empty(ProductionSafety.Problems(Config(new() {
            ["Jwt:SigningKey"] = ProductionSafety.ExampleSigningKey,
        }), new Env("Development")));
        Assert.NotEmpty(ProductionSafety.Problems(Config(new() { ["Jwt:SigningKey"] = "  " }), new Env("Staging")));
    }

    // ---------- Problem Details ----------

    [Fact]
    public async Task UnhandledExceptionsReturnProblemDetailsWithATraceIdAndNoExceptionDetail() {
        await using var app = await StartAsync(new(), routes => routes.MapGet("/boom",
            (Func<IResult>)(() => throw new InvalidOperationException("secret-internal-detail"))));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/boom", Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(Token);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(500, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("secret-internal-detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    // ---------- Forwarded headers ----------

    [Fact]
    public async Task ForwardedClientAddressAndSchemeAreHonouredFromAKnownNetwork() {
        await using var app = await StartAsync(Forwarding("172.16.0.0/12"), MapEcho);

        var echo = await EchoAsync(app, IPAddress.Parse("172.18.0.5"), "203.0.113.7", "https");

        Assert.Equal("203.0.113.7|https", echo);
    }

    [Fact]
    public async Task ForwardedHeadersFromAnUnknownNetworkAreIgnored() {
        await using var app = await StartAsync(Forwarding("172.16.0.0/12"), MapEcho);

        var echo = await EchoAsync(app, IPAddress.Parse("198.51.100.20"), "203.0.113.7", "https");

        Assert.Equal("198.51.100.20|http", echo);
    }

    [Fact]
    public async Task ForwardedHeadersAreIgnoredUnlessEnabled() {
        await using var app = await StartAsync(new(), MapEcho);

        Assert.Equal("172.18.0.5|http", await EchoAsync(app, IPAddress.Parse("172.18.0.5"), "203.0.113.7", "https"));
    }

    [Fact]
    public async Task TwoHopsUnwindCaddyThenNginxButNotAClientSuppliedAddress() {
        var settings = Forwarding("172.16.0.0/12");
        settings["ForwardedHeaders:ForwardLimit"] = "2";
        await using var app = await StartAsync(settings, MapEcho);

        // nginx (172.18.0.4) appends Caddy's address (172.18.0.3) to the client address Caddy set.
        Assert.Equal("203.0.113.7|https", await EchoAsync(app, IPAddress.Parse("172.18.0.4"), "203.0.113.7, 172.18.0.3", "https"));
        // A spoofed entry left of a public client address is never reached: the public hop is not trusted.
        Assert.Equal("203.0.113.7|https",
            await EchoAsync(app, IPAddress.Parse("172.18.0.4"), "10.9.9.9, 203.0.113.7, 172.18.0.3", "https"));
    }

    [Fact]
    public void EnablingForwardedHeadersWithoutTrustedProxiesIsRefused() {
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAegisOpsForwardedHeaders(Config(new() { ["ForwardedHeaders:Enabled"] = "true" })));
        Assert.Contains("KnownNetworks", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => services.AddAegisOpsForwardedHeaders(Config(new() {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownNetworks:0"] = "not-a-cidr",
        })));
    }

    // ---------- Helpers ----------

    private static Dictionary<string, string?> Forwarding(string network) => new() {
        ["ForwardedHeaders:Enabled"] = "true",
        ["ForwardedHeaders:KnownNetworks:0"] = network,
    };

    private static void MapEcho(WebApplication routes) => routes.MapGet("/echo", (Func<HttpContext, string>)(http =>
        $"{http.Connection.RemoteIpAddress}|{http.Request.Scheme}"));

    private static async Task<string> EchoAsync(WebApplication app, IPAddress remote, string forwardedFor, string forwardedProto) {
        var context = await app.GetTestServer().SendAsync(http => {
            http.Request.Method = HttpMethods.Get;
            http.Request.Path = "/echo";
            http.Request.Scheme = "http";
            http.Connection.RemoteIpAddress = remote;
            http.Request.Headers["X-Forwarded-For"] = forwardedFor;
            http.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        }, Token);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync(Token);
    }

    /// <summary>Same middleware order as Program.cs: forwarded headers, then the exception handler.</summary>
    private static async Task<WebApplication> StartAsync(Dictionary<string, string?> settings, Action<WebApplication> map) {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAegisOpsForwardedHeaders(builder.Configuration);
        builder.Services.AddApiProblemDetails();
        var app = builder.Build();
        app.UseAegisOpsForwardedHeaders(app.Configuration);
        app.UseApiExceptionHandler();
        map(app);
        await app.StartAsync(Token);
        return app;
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class Env(string name) : IHostEnvironment {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AegisOps.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
