using AegisOps.Domain.Deploy;
using AegisOps.Domain.Jobs;
using AegisOps.Domain.Policy;

namespace AegisOps.Application.Tests;

public sealed class DeploymentStateTests {
    [Fact]
    public void Approval_quorum_then_simulated_success() {
        var now = DateTimeOffset.UtcNow;
        var deployment = Deployment.Request(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now, "corr", Guid.CreateVersion7(), null, "demo");
        deployment.BeginEvaluation();
        deployment.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, Guid.CreateVersion7(), 2, "2 approval(s) required."), now);
        Assert.Equal(DeploymentStatus.AwaitingApproval, deployment.Status);

        deployment.ReceiveApproval();
        Assert.Equal(1, deployment.ApprovalsReceived);
        deployment.ReceiveApproval();
        deployment.MarkApproved();
        deployment.Start(now);
        deployment.Succeed(now);
        Assert.Equal(DeploymentStatus.Succeeded, deployment.Status);
        Assert.True(deployment.IsTerminal);
    }

    [Fact]
    public void Requester_path_can_cancel_while_waiting() {
        var now = DateTimeOffset.UtcNow;
        var deployment = Deployment.Request(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now, "corr", Guid.CreateVersion7(), null, null);
        deployment.BeginEvaluation();
        deployment.ApplyDecision(new PolicyOutcome(PolicyDecision.RequireApproval, Guid.CreateVersion7(), 1, null), now);
        deployment.Cancel(now);
        Assert.Equal(DeploymentStatus.Cancelled, deployment.Status);
    }

    [Fact]
    public void Stale_running_job_returns_to_the_queue() {
        var now = DateTimeOffset.UtcNow;
        var job = Job.Enqueue(JobType.EvaluateDeployment, """{"deploymentId":"00000000-0000-0000-0000-000000000001"}""", now, "corr");
        job.MarkRunning(now, "worker");
        Assert.False(job.ReleaseIfStale(now.AddMinutes(1), TimeSpan.FromMinutes(2)));
        Assert.True(job.ReleaseIfStale(now.AddMinutes(3), TimeSpan.FromMinutes(2)));
        Assert.Equal(JobStatus.Pending, job.Status);
    }

    [Fact]
    public void Fifth_failure_dead_letters() {
        var now = DateTimeOffset.UtcNow;
        var job = Job.Enqueue(JobType.ExecuteDeployment, """{"deploymentId":"00000000-0000-0000-0000-000000000001"}""", now, "corr");
        for (var attempt = 0; attempt < 5; attempt++) {
            job.MarkRunning(now, "worker");
            job.Fail("boom", now);
        }

        Assert.Equal(JobStatus.DeadLettered, job.Status);
    }
}
