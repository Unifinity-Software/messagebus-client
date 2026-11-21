using MessageBus.Cli.Runner;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Roles;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Mcp;
using MessageBus.Mcp;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class ProposalAuthorityTests
{
    [Fact]
    public async Task Forced_CLI_calls_use_the_worker_pipe_and_are_refused_without_server_traffic()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() && InsideRun.Now, "Windows task sandbox blocks named-pipe clients; run outside this sandbox.");
        using var machine = new TestMachine();
        await using var h = new RunnerHarness();
        using var workspace = RunWorkspace.Create(h.WorkRoot, "dlv_pipe", 1);
        using var http = new HttpClient(machine.Server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url + "/") };
        var pipes = new PipeRunTools((request, stream, ct) => LocalServer.RunAsync(new LocalServerOptions
        {
            Http = http, Links = http, WorkspaceId = "ws_test", MachineId = "mch_test", ConfigDirectory = machine.MbusFolder,
            Input = stream, Output = stream, Delivery = request.Delivery, ProposalAuthority = request.ProposalAuthority,
            Scope = RunToolScope.For(request.Delivery, workspace.Root) with { Tools = null, ProposalAuthority = request.ProposalAuthority },
        }, ct), TextWriter.Null);
        await using var session = await pipes.StartAsync(new RunToolsRequest(workspace, h.Task("dlv_pipe"), new PermissionProfile { Name = "worker" }, launch => launch), TestContext.Current.CancellationToken);
        var result = await PipeRunTools.CallAsync(session.Variables[RunToolsVariables.File], McpToolNames.GetRoleProposal,
            new System.Text.Json.Nodes.JsonObject { ["proposalId"] = "rpr_test" }, TestContext.Current.CancellationToken);
        Assert.Equal(McpErrorCodes.Forbidden, result.Error?.Code);
        Assert.Empty(machine.Server.Requests);
    }

    [Theory]
    [InlineData("original", true)]
    [InlineData("legacy", false)]
    [InlineData("worker", false)]
    [InlineData("machine", false)]
    [InlineData("agent", false)]
    [InlineData("log", false)]
    [InlineData("inbox", false)]
    public async Task Only_the_verified_original_interactive_session_gets_proposal_authority(string variant, bool allowed)
    {
        var capture = new CapturedTools();
        var owner = new RunnerPolicy { OwnerSubjectId = RunnerHarness.Owner, AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.OwnProjects } };
        await using var h = new RunnerHarness(policy: owner, extensions: e => e with { Tools = capture });
        var folder = Directory.CreateDirectory(Path.Combine(h.Scratch, "coordinator")).FullName;
        var logFolder = Directory.CreateDirectory(Path.Combine(h.Home, ".claude", "projects",
            System.Text.RegularExpressions.Regex.Replace(Path.GetFullPath(folder), "[^A-Za-z0-9]", "-"))).FullName;
        if (variant != "log") File.WriteAllText(Path.Combine(logFolder, "session-original.jsonl"), "{}\n");
        h.Sessions.Save(new SessionRecord
        {
            InboxId = "inb_original", WorkspaceId = "ws_test", AgentId = variant == "agent" ? "agt_other" : "claude-code", AgentKind = "claude-code",
            Folder = folder, SessionId = "session-original", MachineId = variant == "machine" ? "mch_other" : "mch_test",
            CoordinatorRecordId = variant == "legacy" ? null : "coord_original",
            OriginAuthority = variant == "legacy" ? ProposalAuthority.Unknown : variant == "worker" ? ProposalAuthority.Worker : ProposalAuthority.Interactive,
            CreatedAt = h.Time.GetUtcNow(), ExpiresAt = h.Time.GetUtcNow().AddHours(-1), CoordinationRecovery = true,
        });
        var task = h.Task("dlv_reply", projectOwner: null, environment: new RunEnvironment
        {
            PermissionProfile = new PermissionProfile { Name = PermissionProfiles.Orchestrator, AllowedTools = ["mcp__mbus__*"] },
        });
        var delivery = task with
        {
            Role = null, Instructions = null, InboxOwner = RunnerHarness.Owner,
            Envelope = task.Envelope with { Target = new InboxTarget { InboxId = variant == "inbox" ? "inb_other" : "inb_original" } },
        };
        Assert.True((await h.DeliverAsync(delivery)).Accepted);
        await h.Service.SettledAsync(delivery.DeliveryId);
        var request = Assert.Single(capture.Requests);
        Assert.Equal(allowed ? ProposalAuthority.ResumedCoordinator : ProposalAuthority.Worker, request.ProposalAuthority);
        if (allowed)
        {
            Assert.Equal("coord_original", request.Session?.CoordinatorRecordId);
            Assert.Equal("session-original", request.Session?.SessionId);
        }
    }

    private sealed class CapturedTools : IRunTools
    {
        public List<RunToolsRequest> Requests { get; } = [];
        public Task<RunToolsSession> StartAsync(RunToolsRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new RunToolsSession(new Dictionary<string, string>(), () => ValueTask.CompletedTask));
        }
    }
}
