using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AegisOps.Api.Identity;
using AegisOps.Domain.Identity;
using AegisOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Api.Tests;

/// <summary>docs/09: a temporary password only lets the user see who they are, change it, refresh or sign out.</summary>
public sealed class PasswordChangeGateTests {
    private static readonly JwtOptions Jwt = new() {
        Issuer = "aegisops-tests",
        Audience = "aegisops-tests",
        SigningKey = "password-change-gate-tests-signing-key-0123456789",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 7,
    };

    [Fact]
    public async Task Temporary_password_tokens_reach_only_the_account_endpoints() {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var user = User.Create("dev@example.test", "Dev", DateTimeOffset.UtcNow);
        var temporary = JwtAccessTokenFactory.Create(user, ["Developer"], Jwt, DateTimeOffset.UtcNow, passwordChangeRequired: true);
        var normal = JwtAccessTokenFactory.Create(user, ["Developer"], Jwt, DateTimeOffset.UtcNow);

        async Task<HttpResponseMessage> Get(string path, string token) {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        using var blocked = await Get("/api/v1/projects", temporary);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("application/problem+json", blocked.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PasswordChangeGate.Reason, problem.RootElement.GetProperty("reason").GetString());

        using var me = await Get("/api/v1/auth/me", temporary);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var change = await Get("/api/v1/auth/change-password", temporary);
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        using var allowed = await Get("/api/v1/projects", normal);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public void Only_tokens_issued_for_a_pending_change_carry_the_claim() {
        var user = User.Create("dev@example.test", "Dev", DateTimeOffset.UtcNow);
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var pending = handler.ReadJsonWebToken(JwtAccessTokenFactory.Create(user, [], Jwt, DateTimeOffset.UtcNow, passwordChangeRequired: true));
        var settled = handler.ReadJsonWebToken(JwtAccessTokenFactory.Create(user, [], Jwt, DateTimeOffset.UtcNow));
        Assert.Equal("true", pending.GetClaim(JwtAccessTokenFactory.PasswordChangeClaim).Value);
        Assert.False(settled.TryGetClaim(JwtAccessTokenFactory.PasswordChangeClaim, out _));
    }

    private static async Task<WebApplication> StartAsync() {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Jwt:Issuer"] = Jwt.Issuer,
            ["Jwt:Audience"] = Jwt.Audience,
            ["Jwt:SigningKey"] = Jwt.SigningKey,
            ["Jwt:AccessTokenMinutes"] = Jwt.AccessTokenMinutes.ToString(CultureInfo.InvariantCulture),
            ["Jwt:RefreshTokenDays"] = Jwt.RefreshTokenDays.ToString(CultureInfo.InvariantCulture),
        });
        builder.Services.AddJwtAuthentication(builder.Configuration);
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UsePasswordChangeGate();
        app.UseAuthorization();
        app.MapGet("/api/v1/auth/me", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/api/v1/auth/change-password", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/api/v1/projects", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
