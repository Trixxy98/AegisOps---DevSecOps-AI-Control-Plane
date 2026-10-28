using System.Net;
using System.Security.Cryptography;
using System.Text;
using AegisOps.Domain.Jobs;
using AegisOps.Infrastructure.Deploy;

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
}
