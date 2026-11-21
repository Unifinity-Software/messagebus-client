using MessageBus.Cli.Agents;
using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class OpenCodeRunTests
{
    [Fact]
    public async Task Supported_local_delivery_executes_with_owned_config_after_loaded_model_preflight()
    {
        using var machine = new MessageBus.Cli.Tests.Infrastructure.TestMachine();
        var env = machine.Env;
        env[OpenCodeAdapter.ConfigContentVariable] = OpenCodeLocalTests.Config(v2: false);
        env["ProgramData"] = Path.Combine(machine.Home, "system");
        var calls = new List<string>();
        await using var h = new RunnerHarness(environment: env, openCodeCommand: RunnerHarness.FakeAgentPath,
            sandbox: _ => new PromptInputSandbox(), extensions: e => e with
            {
                Adapters = _ => OpenCodeAdapter.ForRunner("1.19.5", env.GetValueOrDefault, machine.Home,
                    () => OpenCodeLocalTests.GoodHandler(calls)),
            });
        var task = h.Task("dlv_supported", "opencode", directives: d => d["env"] = "OPENCODE_CONFIG_CONTENT,OPENCODE_DISABLE_PROJECT_CONFIG,XDG_DATA_HOME")
            with { Model = "local/publisher/coder" };
        Assert.True((await h.DeliverAsync(task)).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync(task.DeliveryId)).Result.Status);
        Assert.Equal(2, calls.Count);
        var work = h.WorkTreeOf(task.DeliveryId);
        Assert.Contains(OpenCodeLocalRuntime.Model, File.ReadAllLines(Path.Combine(work, "fake-agent-args.txt")));
        var recorded = File.ReadAllLines(Path.Combine(work, "env.txt"));
        var config = System.Text.Json.Nodes.JsonNode.Parse(recorded.Single(l => l.StartsWith("OPENCODE_CONFIG_CONTENT=", StringComparison.Ordinal))["OPENCODE_CONFIG_CONTENT=".Length..])!;
        Assert.Equal(OpenCodeLocalRuntime.Model, config["small_model"]!.GetValue<string>());
        Assert.Single(config["provider"]!.AsObject());
        Assert.Contains("OPENCODE_DISABLE_PROJECT_CONFIG=true", recorded);
        Assert.StartsWith("XDG_DATA_HOME=" + Path.GetDirectoryName(work), recorded.Single(l => l.StartsWith("XDG_DATA_HOME=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("local/publisher/coder")]
    [InlineData("local/alternative")]
    public async Task Unverified_local_confinement_is_refused_after_approval_before_checkout_without_poisoning_cloud(string model)
    {
        var env = new Dictionary<string, string> { [OpenCodeAdapter.ConfigContentVariable] = OpenCodeLocalTests.Config().Replace("\"hidden\":", "\"alternative\":{\"modelID\":\"api-b\"},\"hidden\":", StringComparison.Ordinal) };
        var calls = 0;
        var approved = false;
        var readiness = new RunnerReadiness(TextWriter.Null);
        await using var h = new RunnerHarness(environment: env, openCodeCommand: RunnerHarness.FakeAgentPath, sandbox: _ => new PromptInputSandbox(),
            extensions: e => e with
            {
                Readiness = readiness,
                Adapters = _ => OpenCodeAdapter.ForRunner("2.3.0", env.GetValueOrDefault, ".", () =>
                {
                    Assert.True(approved);
                    Interlocked.Increment(ref calls);
                    return OpenCodeLocalTests.DownHandler();
                }),
            });
        h.Service.OnRunApproval = _ =>
        {
            approved = true;
            return new RunApprovalState { ApprovalId = "apr_local", Status = RunApprovalStatus.Approved };
        };
        var local = h.Task("dlv_local", "opencode", environment: new RunEnvironment { Checkout = CheckoutMode.Empty, SetupCommand = "exit 99" })
            with { Model = model, RequireRunApproval = true };
        Assert.True((await h.DeliverAsync(local)).Accepted);
        var refused = await h.Service.SettledAsync(local.DeliveryId);
        Assert.Equal(DeclineReason.MachineNotReady, refused.Declined);
        Assert.Equal(SettleStatus.Abandoned, refused.Result.Status);
        Assert.True(approved);
        Assert.Contains("local-only inference is not verified", refused.Result.Error);
        Assert.False(Directory.Exists(Path.GetDirectoryName(h.WorkTreeOf(local.DeliveryId))));
        Assert.Equal(0, calls);
        var cloud = h.Task("dlv_cloud", "opencode") with { Model = "cloud/paid" };
        Assert.True((await h.DeliverAsync(cloud)).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await h.Service.SettledAsync(cloud.DeliveryId)).Result.Status);
        Assert.Equal(0, calls);
    }

    // The fake CLI reads its directives from stdin; real OpenCode reads the saved prompt file.
    private sealed class PromptInputSandbox : IRunSandbox
    {
        public bool IsolationEnabled => false;
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) =>
            launch with { StandardInput = File.ReadAllText(workspace.PromptFile) };
    }
}
