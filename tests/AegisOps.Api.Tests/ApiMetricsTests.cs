using AegisOps.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AegisOps.Api.Tests;

public sealed class ApiMetricsTests
{
    [Fact]
    public async Task Scrape_is_internal_and_uses_route_labels_without_counting_scrapes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiMetrics();
        await using var app = builder.Build();
        // TestServer has no socket. Simulate each listener's LocalPort, not a Host header.
        app.Use((context, next) => {
            context.Connection.LocalPort = context.Request.Headers.ContainsKey("X-Test-Management-Port")
                ? ApiMetrics.ScrapePort : 8080;
            return next(context);
        });
        app.UseApiMetrics();
        app.MapGet("/api/items/{id}", (int id) => Results.Ok(id));
        app.MapGet("/api/fail", () => Results.StatusCode(500));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var client = app.GetTestClient();
        Assert.Equal(System.Net.HttpStatusCode.OK,
            (await client.GetAsync("/api/items/123", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.InternalServerError,
            (await client.GetAsync("/api/fail", TestContext.Current.CancellationToken)).StatusCode);

        using var publicScrape = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, publicScrape.StatusCode);

        using var wrongManagementPath = new HttpRequestMessage(HttpMethod.Get, "/api/items/456");
        wrongManagementPath.Headers.Add("X-Test-Management-Port", "1");
        using var hiddenResponse = await client.SendAsync(wrongManagementPath, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, hiddenResponse.StatusCode);

        using var scrape = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        scrape.Headers.Add("X-Test-Management-Port", "1");
        using var scrapeResponse = await client.SendAsync(scrape, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, scrapeResponse.StatusCode);
        var body = await scrapeResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("http_server_request_duration_seconds_count", body);
        Assert.Contains("http_route=\"/api/items/{id}\"", body);
        Assert.Contains("http_response_status_code=\"500\"", body);
        Assert.DoesNotContain("http_route=\"/metrics\"", body);
        Assert.DoesNotContain("http_route=\"/api/items/123\"", body);
    }
}
