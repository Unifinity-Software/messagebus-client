using System.Net;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class SuspensionTests
{
    [Fact]
    public async Task Rechecks_log_suspension_and_reinstatement_once_each()
    {
        using var log = new StringWriter();
        await using var h = new RunnerHarness(log: log, heartbeat: TimeSpan.FromDays(30));
        var status = new WorkspaceSuspensionStatus { Message = "Under review", Reference = "SUS-ABC234", SupportEmail = "help@example.com" };
        h.Service.OnHeartbeat = _ => new HeartbeatAck { Suspension = status };
        await h.Engine.HeartbeatAsync();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        await h.Engine.HeartbeatAsync();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        await h.Engine.HeartbeatAsync();
        h.Service.OnHeartbeat = _ => new HeartbeatAck();
        h.Time.Advance(TimeSpan.FromMinutes(3));
        await h.Engine.HeartbeatAsync();
        await h.Engine.HeartbeatAsync();
        var lines = log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Only these two lines are counted: after reinstatement the runner refreshes itself in the background and
        // may log about that (its work policy, or a failed refresh) before this reads the log.
        Assert.Single(lines, l => l.Contains("This workspace is suspended: Under review (SUS-ABC234). Contact help@example.com."));
        Assert.Single(lines, l => l.Contains("Workspace reinstated (SUS-ABC234)"));
        Assert.Equal(5, h.Service.Heartbeats.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cli_refusal_uses_message_reference_and_support_from_header_or_body(bool header)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(header
                ? """{"message":"Under review","reference":"SUS-ABC234","supportEmail":"help@example.com"}"""
                : """{"code":"workspace-suspended","message":"Under review","reference":"SUS-ABC234","supportEmail":"help@example.com"}"""),
        };
        if (header) response.Headers.Add("X-Mbus-Error", "workspace-suspended");
        var error = await Assert.ThrowsAsync<MessageBus.Cli.CliException>(() => ServerConnection.ReadEnrollAsync(response, TestContext.Current.CancellationToken));
        Assert.Equal("This workspace is suspended: Under review (SUS-ABC234). Contact help@example.com.", error.Message);
    }
    private static HttpResponseMessage Paused() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("""{"code":"workspace-suspended","message":"Under review","reference":"SUS-ABC234","supportEmail":"help@example.com"}"""),
    };

    [Fact]
    public async Task Startup_refused_during_suspension_registers_again_and_accepts_after_reinstatement()
    {
        await using var h = new RunnerHarness(heartbeat: TimeSpan.FromDays(30));
        var paused = true;
        var policyRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Service.OnRequest = path =>
        {
            if (path == MessageBus.Contracts.Http.ApiRoutes.MachineInventory)
                return paused ? Paused() : new(HttpStatusCode.OK) { Content = new StringContent("""[{"agentId":"agt_recovered","machineId":"mch_test","ownerSubjectId":"microsoft:owner","kind":"codex","displayName":"Recovered","presence":"online"}]""") };
            if (path == ServiceRoutes.Policy)
            {
                if (paused) return Paused();
                policyRead.TrySetResult();
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"ownerSubjectId":"microsoft:owner","acceptsWorkFrom":{"mode":"workspace"}}""") };
            }
            return null;
        };
        await Assert.ThrowsAsync<WorkspaceSuspendedException>(() => h.Engine.RescanAsync(TestContext.Current.CancellationToken));
        Assert.False(await h.Engine.RefreshPolicyAsync(TestContext.Current.CancellationToken));
        h.Service.OnHeartbeat = _ => new HeartbeatAck { Suspension = new() { Reference = "SUS-ABC234", Message = "Review", SupportEmail = "help@example.com" } };
        await h.Engine.HeartbeatAsync();
        paused = false;
        h.Time.Advance(TimeSpan.FromMinutes(2));
        h.Service.OnHeartbeat = _ => new HeartbeatAck();
        await h.Engine.HeartbeatAsync();
        await policyRead.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains(h.Engine.RegistryAgents, a => a.AgentId == "agt_recovered");
        h.Service.OnRunApproval = _ => new() { ApprovalId = "apr_no", Status = RunApprovalStatus.Declined };
        var decision = await h.DeliverAsync(h.Task("dlv_recovered", agent: "agt_recovered") with { RequireRunApproval = true });
        Assert.True(decision.Accepted);
        await h.Service.SettledAsync("dlv_recovered"); // approval declined: no agent process starts
    }

    [Fact]
    public async Task Suspended_approval_wait_asks_once_per_heartbeat_interval()
    {
        await using var h = new RunnerHarness(heartbeat: TimeSpan.FromMinutes(1));
        var requests = 0;
        var paused = true;
        h.Service.OnRequest = path =>
        {
            if (path != ServiceRoutes.RunApproval) return null;
            Interlocked.Increment(ref requests);
            return paused ? Paused() : new(HttpStatusCode.OK)
            { Content = new StringContent("""{"approvalId":"apr_no","status":"declined"}""") };
        };
        Assert.True((await h.DeliverAsync(h.Task("dlv_approval_paused") with { RequireRunApproval = true })).Accepted);
        await Until(() => Volatile.Read(ref requests) == 1);
        await RecheckScheduled(h);
        h.Time.Advance(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 20; i++) await Task.Yield();
        Assert.Equal(1, Volatile.Read(ref requests));
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await Until(() => Volatile.Read(ref requests) == 2);
        await RecheckScheduled(h);
        paused = false;
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Service.SettledAsync("dlv_approval_paused");
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Suspension_stop_does_not_settle_an_already_released_delivery(int stop)
    {
        using var log = new StringWriter();
        await using var h = new RunnerHarness(log: log, heartbeat: TimeSpan.FromDays(30));
        h.Service.OnRunApproval = _ => new() { ApprovalId = "apr_pending", Status = RunApprovalStatus.Pending };
        Assert.True((await h.DeliverAsync(h.Task("dlv_stopped") with { RequireRunApproval = true })).Accepted);
        await h.Service.Progress.Reader.ReadAsync(TestContext.Current.CancellationToken);
        if (stop != 1)
        {
            h.Service.OnHeartbeat = _ => new HeartbeatAck
            {
                Suspension = new() { Reference = "SUS-ABC234", Message = "Review", SupportEmail = "help@example.com", StopRunningTasks = stop == 0 },
                Leases = stop == 2 ? [new() { DeliveryId = "dlv_stopped", LeaseExpiresAt = h.Time.GetUtcNow() }] : [], // the server has already released it
            };
            await h.Engine.HeartbeatAsync();
        }
        else Assert.True(h.Engine.Cancel(new() { ThreadId = "thr_dlv_stopped", DeliveryId = "dlv_stopped", Reason = "Workspace suspended (SUS-ABC234): Security review" }));
        await Until(() => h.Engine.GetStatus().ActiveDeliveryIds.Count == 0);
        Assert.Empty(h.Service.Settles);
        Assert.Contains("Stopped because the workspace was suspended (SUS-ABC234)", log.ToString());
        Assert.DoesNotContain("Couldn't settle", log.ToString());
    }

    private static async Task RecheckScheduled(RunnerHarness h)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await h.Time.Delays.Reader.ReadAsync(timeout.Token) != TimeSpan.FromMinutes(1)) { }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

}
