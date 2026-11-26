using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Xunit;
using Microsoft.Extensions.Time.Testing;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// A runner for one test: the real engine and route handlers, a temp work folder, a fake clock, the
/// fake agent (tests/…/Runner/FakeAgent) as the claude and codex commands, and a recording service
/// behind the real HTTP service client.
/// </summary>
internal sealed class RunnerHarness : IAsyncDisposable
{
    public const string Owner = "microsoft:owner";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbus-runner-tests", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http;

    public RunnerHarness(int slots = 2, RunnerPolicy? policy = null, IBundleSource? bundles = null, Func<string, IRunSandbox>? sandbox = null,
        IReadOnlyDictionary<string, string>? environment = null, IToolSetupRequests? toolSetup = null,
        IReadOnlyDictionary<string, string>? agentEnvironment = null, Func<RunnerExtensions, RunnerExtensions>? extensions = null,
        bool agentsInstalled = false, IReadOnlyDictionary<string, bool>? roleKeysReadable = null, TextWriter? log = null, TimeSpan? heartbeat = null, bool keepRunFolders = true,
        string? openCodeCommand = null, (int CpuCount, long MemoryBytes)? machineSize = null)
    {
        Directory.CreateDirectory(_root);
        _http = new HttpClient(Service) { BaseAddress = new Uri("http://127.0.0.1:9/") };
        var options = new RunnerOptions
        {
            Slots = slots,
            DefaultHeartbeat = heartbeat ?? TimeSpan.FromSeconds(60),
            WorkRoot = WorkRoot,
            // Most tests look at what a run left; the ones about deleting it (the original issue) turn this off.
            KeepRunFolders = keepRunFolders,
            AgentCommands = new Dictionary<AgentKind, string>
            {
                [AgentKind.ClaudeCode] = FakeAgentPath,
                [AgentKind.Codex] = FakeAgentPath,
            },
        };
        if (openCodeCommand is not null) ((Dictionary<AgentKind, string>)options.AgentCommands)[AgentKind.OpenCode] = openCodeCommand;
        var (cpuCount, memoryBytes) = machineSize ?? (Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        Engine = new RunnerEngine(options, new TestHost(policy ?? RunnerPolicy.Workspace, Home, environment, agentEnvironment, agentsInstalled, roleKeysReadable), new HttpRunnerServiceClient(_http), Time, log ?? TextWriter.Null,
            (extensions ?? (e => e)).Invoke(new RunnerExtensions
            {
                Bundles = bundles, Sandbox = sandbox?.Invoke(WorkRoot) ?? new NoSandbox(), Sessions = Sessions, ToolSetup = toolSetup,
            }), cpuCount, memoryBytes);
        Routes = new RunnerRouteDispatcher(Engine, TextWriter.Null);
    }

    public static string FakeAgentPath { get; } = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-agent.exe" : "fake-agent");

    public string WorkRoot => Path.Combine(_root, "runs");

    /// <summary>The runner's home folder (Codex's settings are read from <c>.codex</c> in it).</summary>
    public string Home => Path.Combine(_root, "home");

    public string Scratch => Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;

    /// <summary>The sessions the machine's mbus tools recorded (the original issue), in its own mbus settings folder.</summary>
    public MessageBus.Mcp.SessionRecords Sessions => _sessions ??= new MessageBus.Mcp.SessionRecords(Path.Combine(_root, "mbus"));

    private MessageBus.Mcp.SessionRecords? _sessions;

    public RunnerTestTime Time { get; } = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    public RecordingService Service { get; } = new();

    public RunnerEngine Engine { get; }

    public RunnerRouteDispatcher Routes { get; }

    /// <summary>The worktree the runner makes for a first attempt.</summary>
    public string WorkTreeOf(string deliveryId) => Path.Combine(WorkRoot, deliveryId + "-1", "work");

    public async Task<DeliveryDecision> DeliverAsync(TaskDelivery delivery)
    {
        var response = await Routes.HandleAsync("POST", RunnerRoutes.Deliver, MessageBusJson.Serialize(delivery), CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        return MessageBusJson.Deserialize<DeliveryDecision>(response.Json!)!;
    }

    /// <summary>
    /// A task for the fake agent; <paramref name="directives"/> become <c>fake-agent:</c> lines in the role instructions.
    /// The agent settles as the local MCP server would (<c>settle=yes</c>) unless a directive says otherwise.
    /// </summary>
    public TaskDelivery Task(string id, string agent = "claude-code", string? projectOwner = Owner, Action<Dictionary<string, string>>? directives = null,
        RunEnvironment? environment = null, IReadOnlyList<AttachmentRef>? attachments = null)
    {
        var lines = new Dictionary<string, string> { ["settle"] = "yes" };
        directives?.Invoke(lines);
        var content = "Do the task well.\n" + string.Concat(lines.Select(d => $"fake-agent: {d.Key}={d.Value}\n"));
        var now = Time.GetUtcNow();
        return new TaskDelivery
        {
            DeliveryId = id,
            Attempt = 1,
            LeaseExpiresAt = now.AddMinutes(30),
            AgentId = agent,
            Role = "developer",
            ProjectOwner = projectOwner,
            HeartbeatSeconds = 30,
            Instructions = new InstructionVersion
            {
                Role = "developer",
                Version = 1,
                Content = content,
                ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                Source = InstructionSource.Dashboard,
                CreatedAt = now,
            },
            RunEnvironment = environment ?? new RunEnvironment { Checkout = CheckoutMode.Empty },
            Envelope = new MessageEnvelope
            {
                Id = "msg_" + id,
                Type = "task.implement",
                Source = "agent://ws_test/agt_sender",
                Time = now,
                WorkspaceId = "ws_test",
                Target = new RoleTarget { Role = "developer" },
                ThreadId = "thr_" + id,
                Title = "Add due dates",
                Sender = new SenderIdentity { Kind = SenderKind.Agent, SubjectId = projectOwner },
                Body = new BodyRef { SizeBytes = 6, Sha256 = "00", Data = "c2VhbGVk" },
                Attachments = attachments ?? [],
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        _http.Dispose();
        KillLeftovers();
        RunWorkspace.TryDelete(_root);
    }

    /// <summary>
    /// If the runner ever failed to kill a fake agent's tree, its orphans would hold the test host's
    /// output open and hang the test run; end them here so a regression fails instead of hanging.
    /// </summary>
    private void KillLeftovers()
    {
        if (!Directory.Exists(WorkRoot))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(WorkRoot, "pids.txt", SearchOption.AllDirectories))
        {
            foreach (var pid in File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    if (process.ProcessName.StartsWith("fake-agent", StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <param name="agentsInstalled">Every agent counts as installed (the fake agent), whatever this machine's PATH has; otherwise
    /// the scan finds what is really installed, which differs between a developer's machine and CI.</param>
    private sealed class TestHost(RunnerPolicy policy, string home, IReadOnlyDictionary<string, string>? environment,
        IReadOnlyDictionary<string, string>? agentEnvironment, bool agentsInstalled, IReadOnlyDictionary<string, bool>? roleKeysReadable) : IRunnerHostContext
    {
        public Uri ServerUrl { get; } = new("http://127.0.0.1:9");
        public string? MachineId => "mch_test";
        public string MachineName => "test-machine";
        public string? Location => "Test bench";
        public RunnerPolicy Policy => policy;
        public IReadOnlyDictionary<string, AgentKind> AgentIds { get; } = new Dictionary<string, AgentKind> { ["agt_codex"] = AgentKind.Codex };
        public string HomeDirectory => home;
        public HttpClient CreateServiceClient() => throw new NotSupportedException();
        public IReadOnlyList<KeyValuePair<string, string>> CreateConnectHeaders(string method, string pathAndQuery) => [];
        public string? LocateCommand(string command) =>
            agentsInstalled && new[] { AgentKind.ClaudeCode, AgentKind.Codex }.Any(k => MessageBus.Cli.Runner.Agents.AgentKinds.Command(k) == command)
                ? FakeAgentPath
                : CommandLocator.Find(command, Environment.GetEnvironmentVariable, []);
        public Task<IReadOnlyDictionary<string, bool>?> CheckRoleKeysAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(roleKeysReadable);
        public Task<MachineInventory> BuildInventoryAsync(int slots, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(RunnerEnvironment.BasicInventory(this, slots, DateTimeOffset.UnixEpoch));
        public string? GetEnvironmentVariable(string name) => environment?.GetValueOrDefault(name);
        public IReadOnlyDictionary<string, string> AgentEnvironment() => agentEnvironment ?? new Dictionary<string, string>();
    }
}

/// <summary>The service side of <see cref="ServiceRoutes"/>: records what the runner sends and answers heartbeats.</summary>
internal sealed class RecordingService : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunnerSettle>> _settled = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RunnerProgress>> _agentProgress = new();

    public Func<string, HttpResponseMessage?>? OnRequest { get; set; }

    public ConcurrentQueue<RunnerHeartbeat> Heartbeats { get; } = new();

    private readonly System.Threading.Channels.Channel<RunnerHeartbeat> _beats = System.Threading.Channels.Channel.CreateUnbounded<RunnerHeartbeat>();

    /// <summary>The next heartbeat to arrive.</summary>
    public async Task<RunnerHeartbeat> NextHeartbeatAsync() =>
        await _beats.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Every progress report, in the order they arrived.</summary>
    public System.Threading.Channels.Channel<RunnerProgress> Progress { get; } = System.Threading.Channels.Channel.CreateUnbounded<RunnerProgress>();

    public ConcurrentQueue<RunnerSettle> Settles { get; } = new();

    private readonly System.Threading.Channels.Channel<MessageBus.Contracts.Releases.RunnerUpdateProgress> _updates =
        System.Threading.Channels.Channel.CreateUnbounded<MessageBus.Contracts.Releases.RunnerUpdateProgress>();

    /// <summary>The next thing the runner says about its update (the original issue).</summary>
    public async Task<MessageBus.Contracts.Releases.RunnerUpdateProgress> NextUpdateProgressAsync() =>
        await _updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

    private readonly System.Threading.Channels.Channel<MachineInventory> _inventories = System.Threading.Channels.Channel.CreateUnbounded<MachineInventory>();

    /// <summary>The next inventory the runner sends.</summary>
    public async Task<MachineInventory> NextInventoryAsync() =>
        await _inventories.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));

    /// <summary>How heartbeats are answered (default: every lease extended).</summary>
    public Func<RunnerHeartbeat, HeartbeatAck> OnHeartbeat { get; set; } = beat => new HeartbeatAck
    {
        Leases = [.. beat.ActiveDeliveryIds.Select(id => new LeaseState { DeliveryId = id, LeaseExpiresAt = DateTimeOffset.MaxValue })],
    };

    /// <summary>How the machine owner's approval of a run is answered (the original issue); default: approved.</summary>
    public Func<RunApprovalRequest, RunApprovalState> OnRunApproval { get; set; } = _ => new RunApprovalState { ApprovalId = "apr_test", Status = RunApprovalStatus.Approved };

    /// <summary>The settle for <paramref name="deliveryId"/>, once it arrives.</summary>
    public Task<RunnerSettle> SettledAsync(string deliveryId) => Slot(_settled, deliveryId).Task.WaitAsync(TimeSpan.FromSeconds(60));

    /// <summary>The first progress report that came from the agent's own output.</summary>
    public Task<RunnerProgress> AgentProgressAsync(string deliveryId) => Slot(_agentProgress, deliveryId).Task.WaitAsync(TimeSpan.FromSeconds(60));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = "/" + request.RequestUri!.AbsolutePath.TrimStart('/');
        if (OnRequest?.Invoke(path) is { } overridden) return overridden;
        switch (path)
        {
            case ServiceRoutes.Heartbeat:
                var beat = MessageBusJson.Deserialize<RunnerHeartbeat>(body)!;
                Heartbeats.Enqueue(beat);
                _beats.Writer.TryWrite(beat);
                return Json(MessageBusJson.Serialize(OnHeartbeat(beat)));
            case MessageBus.Contracts.Http.ApiRoutes.MachineInventory:
                _inventories.Writer.TryWrite(MessageBusJson.Deserialize<MachineInventory>(body)!);
                return Json("[]");
            case ServiceRoutes.UpdateProgress:
                _updates.Writer.TryWrite(MessageBusJson.Deserialize<MessageBus.Contracts.Releases.RunnerUpdateProgress>(body)!);
                break;
            case ServiceRoutes.RunApproval:
                return Json(MessageBusJson.Serialize(OnRunApproval(MessageBusJson.Deserialize<RunApprovalRequest>(body)!)));
            case ServiceRoutes.Settle:
                var settle = MessageBusJson.Deserialize<RunnerSettle>(body)!;
                Settles.Enqueue(settle);
                Slot(_settled, settle.DeliveryId).TrySetResult(settle);
                break;
            case ServiceRoutes.Progress:
                var progress = MessageBusJson.Deserialize<RunnerProgress>(body)!;
                Progress.Writer.TryWrite(progress);
                // The runner's own notes ("Preparing…", "Started …") come first; the agent's follow.
                if (!progress.Progress.Message.StartsWith("Preparing", StringComparison.Ordinal)
                    && !progress.Progress.Message.StartsWith("Started ", StringComparison.Ordinal)
                    && !progress.Progress.Message.StartsWith("Running the setup", StringComparison.Ordinal))
                {
                    Slot(_agentProgress, progress.DeliveryId).TrySetResult(progress);
                }
                break;
        }
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TaskCompletionSource<T> Slot<T>(ConcurrentDictionary<string, TaskCompletionSource<T>> slots, string id) =>
        slots.GetOrAdd(id, _ => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously));
}

internal sealed class RunnerTestTime(DateTimeOffset start) : FakeTimeProvider(start)
{
    public System.Threading.Channels.Channel<TimeSpan> Delays { get; } = global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (period == Timeout.InfiniteTimeSpan) Delays.Writer.TryWrite(dueTime);
        return timer;
    }
}
