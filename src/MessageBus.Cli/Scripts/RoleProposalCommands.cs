using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Mcp;

namespace MessageBus.Cli.Scripts;

internal static class RoleProposalCommands
{
    public static void AddTo(Command roles, CliHost host, CommonOptions options)
    {
        var input = new Option<string>("--input") { Required = true, Description = "JSON file (- for stdin): {operation, definition, rationale, idempotencyKey}." };
        var agent = new Option<string?>("--agent") { Description = "An agent enrolled on this machine, by id or kind." };
        var proposeJson = ScriptRunner.JsonOption();
        var proposeInbox = InboxOption();
        var propose = new Command("propose", "Submit a role definition for editable owner review in the dashboard.") { input, agent, proposeInbox, proposeJson };
        propose.SetAction((parse, ct) => RunAsync(host, parse, options, McpToolNames.ProposeRole, parse.GetValue(proposeJson),
            parse.GetValue(proposeInbox), parse.GetValue(agent), async () =>
            {
                var file = parse.GetValue(input)!;
                var text = file == "-" ? await host.In.ReadToEndAsync(ct) : await File.ReadAllTextAsync(Path.GetFullPath(file, host.CurrentDirectory), ct);
                return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("--input must contain a JSON object.");
            }, ct));
        roles.Subcommands.Add(propose);

        foreach (var wait in new[] { false, true })
        {
            var id = new Argument<string>("proposal") { Description = "The proposal id." };
            var inbox = InboxOption();
            var timeout = new Option<int>("--timeout") { DefaultValueFactory = _ => 30, Description = "Wait 0–30 seconds; pending at the deadline is not failure." };
            var json = ScriptRunner.JsonOption();
            var command = new Command(wait ? "wait-proposal" : "get-proposal", "Read authoritative state for this original coordinator's proposal.") { id, inbox, json };
            if (wait) command.Options.Add(timeout);
            command.SetAction((parse, ct) => RunAsync(host, parse, options, wait ? McpToolNames.WaitRoleProposal : McpToolNames.GetRoleProposal,
                parse.GetValue(json), parse.GetValue(inbox), null, () =>
                {
                    var args = new JsonObject { ["proposalId"] = parse.GetValue(id) };
                    if (wait) args["timeoutSeconds"] = parse.GetValue(timeout);
                    return Task.FromResult(args);
                }, ct));
            roles.Subcommands.Add(command);
        }
    }

    private static Option<string?> InboxOption() => new("--inbox")
    {
        Description = "Original local coordinator inbox from submission (required for later CLI reads). Runner sessions always use their own binding.",
    };

    private static async Task<int> RunAsync(CliHost host, ParseResult parse, CommonOptions options, string tool, bool json,
        string? inbox, string? agent, Func<Task<JsonObject>> arguments, CancellationToken ct)
    {
        string? coordinatorInbox = null;
        using var retryLease = new RetryLease();
        try
        {
            ScriptResult result;
            if (LocalServer.HasRunMarkers(host.GetEnvironmentVariable))
            {
                // Resolve the runner pipe before settings, files, capabilities or machine authentication.
                if (host.GetEnvironmentVariable(RunToolsVariables.File) is not { Length: > 0 } file)
                    return await FailAsync(host, json, new McpError { Code = McpErrorCodes.Forbidden, Message = "This run has no tools pipe; direct machine authentication is refused." });
                if (inbox is not null || agent is not null)
                    return await FailAsync(host, json, new McpError { Code = McpErrorCodes.Forbidden, Message = "A run cannot replace its original coordinator or agent binding." });
                result = await PipeRunTools.CallAsync(file, tool, await arguments(), ct);
            }
            else
            {
                var context = CommandContext.Load(host, parse, options);
                var settings = context.Settings;
                if (settings.Server is null || settings.ApiKey is not null || settings.Stored.MachineId is null
                    || !settings.CanAuthenticate(context.Store) || settings.Stored.WorkspaceId is not { } workspace)
                    return await FailAsync(host, json, new McpError { Code = McpErrorCodes.Forbidden, Message = "Role proposals require an enrolled machine connection." });
                var args = await arguments();
                var sessions = new SessionRecords(context.Store.Directory);
                string? retryKey = null;
                if (tool == McpToolNames.ProposeRole && args.FirstOrDefault(p => p.Key.Equals("idempotencyKey", StringComparison.OrdinalIgnoreCase)).Value
                    is JsonValue value && value.TryGetValue<string>(out var key) && !string.IsNullOrWhiteSpace(key))
                {
                    // Include the connection and requested agent selector; changed request content still reaches
                    // the server under this same key so it can return an idempotency conflict.
                    var identity = new JsonArray(settings.Server.ToString(), workspace, settings.Stored.MachineId, agent, key).ToJsonString();
                    retryKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    retryLease.Acquire(context.Store.Directory, retryKey);
                }
                var bound = retryKey is null ? null : sessions.ForRoleProposalRetry(workspace, settings.Stored.MachineId, retryKey);
                if (bound is not null && inbox is not null && bound.InboxId != inbox)
                    return await FailAsync(host, json, new McpError { Code = McpErrorCodes.Conflict,
                        Message = "This retry key is already bound to another original coordinator inbox." }, bound.InboxId);
                var record = inbox is null ? bound : sessions.Find(inbox);
                var retryRecord = inbox is null && record is not null;
                if ((inbox is not null && (record is null || record.OriginAuthority != ProposalAuthority.Interactive
                    || record.WorkspaceId != workspace || record.MachineId != settings.Stored.MachineId))
                    || (record is not null && (record.OriginAuthority != ProposalAuthority.Interactive || string.IsNullOrWhiteSpace(record.CoordinatorRecordId)))
                    || (tool != McpToolNames.ProposeRole && record is null))
                    return await FailAsync(host, json, new McpError { Code = McpErrorCodes.Forbidden, Message = "Give the original local coordinator inbox with --inbox." });
                if (record is not null && !retryRecord && agent is not null && agent != record.AgentId)
                    return await FailAsync(host, json, new McpError { Code = McpErrorCodes.Forbidden, Message = "The agent must match the original coordinator record." });
                using var http = ServerConnection.CreateAuthenticatedClient(host, settings, context.Store, "cli");
                await using var session = new ScriptSession(LocalServer.Options(http, workspace, settings.Stored.MachineId, context.Store.Directory,
                    Stream.Null, Stream.Null, host.Error, CliHost.Version, host.GetEnvironmentVariable) with
                {
                    Session = record, Agent = record?.AgentId ?? agent, MachineKeyPem = context.Store.LoadKeyPem,
                    RoleProposalRetryKey = retryKey,
                    WorkingDirectory = host.CurrentDirectory, Time = host.Time,
                });
                try { result = await session.CallAsync(tool, args, ct); }
                finally { coordinatorInbox = session.CoordinatorInboxId; }
            }
            if (result.Error is { } error) return await FailAsync(host, json, error, coordinatorInbox);
            var output = JsonNode.Parse(result.Value.GetRawText())!.AsObject();
            if (coordinatorInbox is not null) output["coordinatorInboxId"] = coordinatorInbox;
            if (json) await host.Out.WriteLineAsync(output.ToJsonString());
            else
            {
                await host.Out.WriteLineAsync($"{Script.Text(result.Value, "proposalId")}: {Script.Text(result.Value, "status")}");
                if (Script.Text(result.Value, "reviewUrl") is { } review) await host.Out.WriteLineAsync(review);
                if (coordinatorInbox is not null) await host.Out.WriteLineAsync($"Coordinator inbox: {coordinatorInbox} (use --inbox for later reads).");
            }
            return 0;
        }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TimeoutException or HttpRequestException or ModelContextProtocol.McpException)
            || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return await FailAsync(host, json, new McpError
            {
                Code = e is JsonException ? McpErrorCodes.InvalidInput : McpErrorCodes.Unavailable,
                Message = e.GetBaseException().Message, Retryable = e is IOException or TimeoutException or HttpRequestException or ModelContextProtocol.McpException or TaskCanceledException,
            }, coordinatorInbox);
        }
    }

    private static async Task<int> FailAsync(CliHost host, bool json, McpError error, string? coordinatorInbox = null)
    {
        await host.Error.WriteLineAsync(error.Message);
        if (json)
        {
            var output = new JsonObject { ["error"] = JsonNode.Parse(MessageBusJson.Serialize(error)) };
            if (coordinatorInbox is not null) output["coordinatorInboxId"] = coordinatorInbox;
            await host.Out.WriteLineAsync(output.ToJsonString());
        }
        else if (coordinatorInbox is not null)
            await host.Error.WriteLineAsync($"Coordinator inbox: {coordinatorInbox} (reuse the original command to retry, or --inbox for later reads).");
        return ScriptExitCodes.For(error.Code);
    }

    // Serialize invocations sharing a retry key so two first attempts cannot open competing records.
    private sealed class RetryLease : IDisposable
    {
        private FileStream? _file;

        public void Acquire(string configDirectory, string key)
        {
            var folder = Path.Combine(configDirectory, "proposal-retries");
            Directory.CreateDirectory(folder);
            _file = new FileStream(Path.Combine(folder, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public void Dispose() => _file?.Dispose();
    }
}
