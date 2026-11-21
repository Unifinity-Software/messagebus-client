using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MessageBus.Mcp;

/// <summary>What the local server needs; <see cref="LocalServer.Options"/> builds it from what <c>mbus mcp</c> prepared.</summary>
public sealed record LocalServerOptions
{
    /// <summary>The service, authenticated as this machine; base address the server URL with a trailing slash.</summary>
    public required HttpClient Http { get; init; }

    /// <summary>Plain client for attachment links (no credentials, long timeout). Made by the server when not given.</summary>
    public HttpClient? Links { get; init; }

    /// <summary>The workspace this machine is enrolled in.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The enrolled machine.</summary>
    public string? MachineId { get; init; }

    /// <summary>mbus's settings folder (key trust records; <c>machine-key.pem</c> when <see cref="MachineKeyPem"/> isn't given).</summary>
    public required string ConfigDirectory { get; init; }

    /// <summary>Reads this machine's device key (PEM) from mbus's key store (the original issue); null reads <c>machine-key.pem</c> in <see cref="ConfigDirectory"/>.</summary>
    public Func<string?>? MachineKeyPem { get; init; }

    /// <summary>
    /// Set when the runner serves one task's tools itself (the original issue): which tools, threads and folders the task may use.
    /// Null for an interactive session or a <c>mbus mcp</c> the agent started with the machine's own credentials.
    /// </summary>
    public RunToolScope? Scope { get; init; }

    /// <summary>Trusted origin. Only explicit interactive or verified resumed coordinator hosts may use proposals.</summary>
    public ProposalAuthority ProposalAuthority { get; init; }

    /// <summary>Reports a non-retryable tool refusal to the runner that owns this server, outside the agent's files.</summary>
    public Action<string, McpError>? Refused { get; init; }

    /// <summary>Where temporary folders (bundles, patches, fetched files) go; null for the system's temp folder.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>Applied to every git this server starts (the runner starts a task's git inside its sandbox, the original issue); null leaves git as it is.</summary>
    public Action<System.Diagnostics.ProcessStartInfo>? PrepareGit { get; init; }

    /// <summary>MCP from the agent.</summary>
    public required Stream Input { get; init; }

    /// <summary>MCP to the agent; nothing else is written to it.</summary>
    public required Stream Output { get; init; }

    /// <summary>Logs.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;

    /// <summary>The mbus version.</summary>
    public string ClientVersion { get; init; } = "0.1.0";

    /// <summary>
    /// <c>MBUS_DELIVERY_FILE</c>: set when a runner started the agent for one task (the original issue). The task's
    /// <see cref="TaskDelivery"/>, body still sealed; <c>inbox</c> returns it decrypted.
    /// </summary>
    public string? DeliveryFile { get; init; }

    /// <summary>
    /// The runner's own copy of the task's delivery, when the runner serves the run's tools (the original issue): used instead of
    /// <see cref="DeliveryFile"/>, which sits in the run's folder where the agent can rewrite it.
    /// </summary>
    public TaskDelivery? Delivery { get; init; }

    /// <summary><c>MBUS_SETTLE_FILE</c>: where <c>settle</c> records the agent's result for the runner.</summary>
    public string? SettleFile { get; init; }

    /// <summary><c>MBUS_AGENT</c>: which of the machine's agents this is (id or kind); else the delivery's, else from the MCP client's name.</summary>
    public string? Agent { get; init; }

    /// <summary>The directory the agent works in; bundles go into it by default.</summary>
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// the original issue: a task's git bundle with no <c>base</c> carries only the commits after the newest one this machine sent that target
    /// for the repository, and this server sends the rest to a runner that asks while it runs. Off for <c>mbus send</c> (the original issue),
    /// which ends before a runner could ask: it sends the whole history unless given <c>--base</c>.
    /// </summary>
    public bool IncrementalBundles { get; init; } = true;

    /// <summary>Longest single long-poll call to the service; longer waits are made of several (proxies and HTTP client timeouts stay happy).</summary>
    public TimeSpan PollChunk { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>
    /// <c>MBUS_MCP_MAX_CALL_SECONDS</c>: longest one <c>wait</c>, <c>watch</c> or <c>inbox</c> call may block. Null: by the
    /// MCP client (<see cref="McpCallLimits"/>: 110 s for Claude Code; for Codex its mbus <c>tool_timeout_sec</c> less a margin,
    /// else 55 s, as for others), so the client never ends the call first.
    /// </summary>
    public int? MaxCallSeconds { get; init; }

    /// <summary>Claude Code's settings folder (its session logs give the orchestrator's usage, the original issue); null for <c>CLAUDE_CONFIG_DIR</c> or <c>~/.claude</c>.</summary>
    public string? ClaudeHome { get; init; }

    /// <summary>Codex's home folder (its session logs give a Codex orchestrator's usage, the original issue); null for <c>CODEX_HOME</c> or <c>~/.codex</c>.</summary>
    public string? CodexHome { get; init; }

    /// <summary>
    /// Set by the runner when it resumed a closed session for a reply to its inbox (the original issue): the session's inbox, so its
    /// sends keep their replies coming there and <c>open_inbox</c> returns it.
    /// </summary>
    public SessionRecord? Session { get; init; }
}

/// <summary>
/// The local <c>mbus mcp</c> server (the original issue): the stdio MCP server an agent's MCP client (Claude Code,
/// Codex) starts. It shows the same tools as the service's <c>/mcp</c> endpoint, plus
/// <c>fetch_attachment</c>, and forwards each call there over the machine's authenticated connection,
/// encrypting on the way out (bodies, replies, attachments, after a secret scan) and decrypting on
/// the way back, so the service only stores ciphertext. An agent the runner started for one task sees
/// only the tools a worker needs (<see cref="ShownTools"/>).
/// </summary>
public static class LocalServer
{
    /// <summary>Environment variable naming the agent behind this server (id <c>agt_…</c> or kind <c>claude-code</c>).</summary>
    public const string AgentVariable = "MBUS_AGENT";

    /// <summary>Options from <c>mbus mcp</c>'s context and the per-run environment the runner sets (the original issue).</summary>
    public static LocalServerOptions Options(HttpClient http, string? workspaceId, string? machineId, string configDirectory,
        Stream input, Stream output, TextWriter log, string clientVersion, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return new LocalServerOptions
        {
            Http = http, WorkspaceId = workspaceId, MachineId = machineId, ConfigDirectory = configDirectory,
            Input = input, Output = output, Log = log, ClientVersion = clientVersion,
            ProposalAuthority = HasRunMarkers(environment) ? ProposalAuthority.Unknown : ProposalAuthority.Interactive,
            DeliveryFile = NonEmpty(environment("MBUS_DELIVERY_FILE")),
            SettleFile = NonEmpty(environment("MBUS_SETTLE_FILE")),
            Agent = NonEmpty(environment(AgentVariable)),
            MaxCallSeconds = int.TryParse(environment("MBUS_MCP_MAX_CALL_SECONDS"), out var most) && most > 0 ? most : null,
            ClaudeHome = SessionUsage.ClaudeHome(environment),
            CodexHome = SessionUsage.CodexHome(environment),
        };

        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Serves MCP on the streams until the input closes or <paramref name="ct"/> fires. Returns the exit code.</summary>
    public static async Task<int> RunAsync(LocalServerOptions options, CancellationToken ct)
    {
        if (options.WorkspaceId is null)
        {
            await options.Log.WriteLineAsync("mbus mcp: this machine isn't enrolled in a workspace. Run mbus login first.");
            return 1;
        }

        using var ownLinks = options.Links is null ? new HttpClient { Timeout = TimeSpan.FromHours(1) } : null;
        await using var tools = new LocalTools(options with { Links = options.Links ?? ownLinks });
        var shown = ShownTools(options.Delivery ?? ReadDelivery(options.DeliveryFile));
        var listed = await tools.ListToolsAsync(shown, ct);
        var server = McpServer.Create(new StreamServerTransport(options.Input, options.Output, "mbus"), new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "mbus", Version = options.ClientVersion },
            ServerInstructions = shown is null ? Instructions : WorkerInstructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = listed }),
                CallToolHandler = async (request, cancel) =>
                    await tools.CallAsync(request.Server.ClientInfo?.Name, request.Params!.Name, request.Params.Arguments, cancel),
            },
        });
        await using (server)
        {
            try
            {
                await server.RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
        return 0;
    }

    /// <summary>
    /// For the runner once a run's agent has ended (the original issue): when the agent's settle was refused (an attachment it could have
    /// fixed) and it never settled after, sends the answer kept from that call, saying what wasn't attached, and records its
    /// result in the run's settle file as the agent's. False when there was nothing to send.
    /// </summary>
    /// <param name="options">The run's tools as the runner serves them (<see cref="LocalServerOptions.DeliveryFile"/> and <see cref="LocalServerOptions.SettleFile"/> set).</param>
    /// <param name="ct">Stops it.</param>
    public static async Task<bool> SendKeptAnswerAsync(LocalServerOptions options, CancellationToken ct)
    {
        if (options.WorkspaceId is null || options.SettleFile is not { } settleFile || !File.Exists(KeptAnswer.KeptFile(settleFile)))
        {
            return false;
        }
        using var ownLinks = options.Links is null ? new HttpClient { Timeout = TimeSpan.FromHours(1) } : null;
        await using var tools = new LocalTools(options with { Links = options.Links ?? ownLinks });
        return await tools.SendKeptAnswerAsync(ct);
    }

    /// <summary>
    /// The tools a session sees (the original issue). An agent the runner started for one task (MBUS_DELIVERY_FILE) gets
    /// <see cref="McpToolCatalog.WorkerTools"/> plus any <c>mcp__mbus__&lt;tool&gt;</c> its role's permission profile
    /// allows (<c>mcp__mbus</c> or <c>mcp__mbus__*</c>: all of them); none of the built-in roles needs more, and every
    /// tool listed is re-read on every model call. A resumed orchestrator (a message to a session inbox, or the
    /// orchestrator profile; the runner gives it that profile too) and interactive sessions get every tool: null.
    /// Role proposal tools additionally require trusted coordinator authority and a supported server, both for discovery and forced calls.
    /// </summary>
    public static IReadOnlySet<string>? ShownTools(TaskDelivery? delivery)
    {
        if (delivery is null) return null;
        var profile = delivery.RunEnvironment.PermissionProfile;
        if (delivery.Envelope.Target is InboxTarget || profile.Name == PermissionProfiles.Orchestrator) return null;
        var shown = new HashSet<string>(McpToolCatalog.WorkerTools, StringComparer.Ordinal);
        const string prefix = "mcp__mbus__";
        foreach (var allowed in profile.AllowedTools)
        {
            if (allowed is "mcp__mbus" or "mcp__mbus__*") return null;
            if (allowed.StartsWith(prefix, StringComparison.Ordinal) && McpToolCatalog.Find(allowed[prefix.Length..]) is { } spec) shown.Add(spec.Name);
        }
        return shown;
    }

    /// <summary>Any relay/delivery marker requires runner provenance, even if its file is absent or corrupt.</summary>
    public static bool HasRunMarkers(Func<string, string?> environment) =>
        new[] { "MBUS_RUN_TOOLS", "MBUS_DELIVERY_FILE", "MBUS_SETTLE_FILE" }.Any(n => environment(n) is not null);

    private static TaskDelivery? ReadDelivery(string? file)
    {
        try
        {
            return file is not null && File.Exists(file) ? MessageBusJson.Deserialize<TaskDelivery>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // the full list, as without a runner
        }
    }

    private const string Instructions =
        "messagebus.ai passes tasks between coding agents on different machines; message bodies are encrypted on this machine. " +
        "To orchestrate work: find_project to discover existing work, choose executionScope (owner-runners or workspace) from the user's explicit/standing preference or ask; preserve it on resume. Call ensure_project with executionScope and claimedWorkItems for every explicit tracker item (continue only on coordinationAcquired; stop on conflict), directory to see roles and agents, open_inbox (answers that come after this session closes resume it), send or send_many tasks to roles, " +
        "wait for results on the thread ids you sent (call again on timeout), retry to resend a failed task, then complete_project and report. Before a risky step or for a person's sign-off: " +
        "request_approval, then wait_approval (only a person can approve). To work on a task you were given: inbox, " +
        "fetch_attachment for its code, progress while working, settle once when done. " + McpToolCatalog.Errors;

    private const string WorkerInstructions =
        "messagebus.ai started you for one task: read it with inbox, fetch its code with fetch_attachment, report progress while you work, settle once when done. " +
        McpToolCatalog.Errors;
}
