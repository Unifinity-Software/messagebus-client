using System.Text.Json;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MessageBus.Cli.Runner;

/// <summary>Returns code before the runner removes the only local copy.</summary>
internal static class RunCodeReturn
{
    private static readonly HttpClient Links = MessageBus.Attachments.Client.AttachmentTransfers.CreateHttpClient();

    public static Func<RunWorkspace, TaskDelivery, string, string?, CancellationToken, Task<bool>>? Create(CommandContext context)
    {
        if (context.Settings.Stored.WorkspaceId is not { } workspace || context.Settings.Server is null) return null;
        var http = ServerConnection.CreateAuthenticatedClient(context.Host, context.Settings, context.Store, "runner");
        return async (run, delivery, branch, @base, ct) =>
        {
            using var keys = new MachineKeys(http, workspace, context.Settings.Stored.MachineId,
                context.Store.Directory, context.Host.Error, context.Host.Time, context.Store.LoadKeyPem);
            return await ReturnAsync(http, Links, workspace, run, delivery, branch, @base, ct, keys.RecipientAsync).ConfigureAwait(false);
        };
    }

    internal static async Task<bool> ReturnAsync(HttpClient http, HttpClient links, string workspace, RunWorkspace run,
        TaskDelivery delivery, string branch, string? baseCommit, CancellationToken ct, Func<MessageTarget?, CancellationToken, Task<PublicKeyInfo>> recipient)
    {
        if (!(run.LocalRepositoryAfterExit ?? RunCommits.IsLocalRepository(run))) throw new InvalidOperationException("The run's repository links to files outside its own checkout; keeping it for recovery.");
        using var gitSettings = Git.PrepareWith(start =>
        {
            var args = start.ArgumentList.ToArray();
            start.ArgumentList.Clear();
            if (run.VerifiedReturnRevision is not null)
            {
                start.FileName = VerificationProcess.Git;
                start.Environment.Clear();
                start.Environment["PATH"] = "/usr/bin:/bin";
                start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
                start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            }
            foreach (var entry in RunCommits.GitEnvironment(run)) start.Environment[entry.Key] = entry.Value;
            MessageBus.Cli.Agents.CodexAuthentication.RemoveKeys(start.Environment);
            if (!RunCommits.IsLocalRepository(run) || !RunCommits.HasSafeConfigAsync(start.FileName, run).GetAwaiter().GetResult())
                throw new InvalidOperationException("The run repository is unsafe; keeping it for recovery.");
            foreach (var arg in RunCommits.GitArguments(run).Concat(args)) start.ArgumentList.Add(arg);
        });
        var task = delivery.Envelope;
        var head = await Git.CommitIdAsync(run.WorkTree, "refs/heads/" + branch, ct).ConfigureAwait(false);
        if (run.VerifiedReturnRevision is { } expected)
        {
            if (head != expected) return false;
        }
        else
        {
            // The settle file excludes attachments. Read the service's accepted replies instead.
            using var response = await http.GetAsync($"api/w/{Uri.EscapeDataString(workspace)}/threads/{Uri.EscapeDataString(task.ThreadId)}/messages", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var messages = document.RootElement.EnumerateArray().Select(e => e.Deserialize<MessageEnvelope>(MessageBusJson.Options)).OfType<MessageEnvelope>();
            // Agents can return bundles without commit refs (as the local settle tool does).
            foreach (var attachment in messages.Where(m => m.Type == task.Type + ".reply" && m.ParentId == task.Id
                && m.Sender.AgentId == delivery.AgentId).SelectMany(m => m.Attachments)
                .Where(a => a.Format == AttachmentFormat.GitBundle))
            {
                if (attachment.Branch == branch) return true;
                if (attachment.Branch is not { Length: > 0 } returnedBranch) continue;
                try
                {
                    if (await Git.CommitIdAsync(run.WorkTree, "refs/heads/" + returnedBranch, ct).ConfigureAwait(false) == head) return true;
                }
                catch (GitException)
                {
                    // A reply may name a branch that is absent from this checkout.
                }
            }
        }

        var target = task.ReplyTo
            ?? (task.Sender is { Kind: SenderKind.Agent, AgentId: { } agent } ? new AgentTarget { AgentId = agent } : task.Target);
        // Use the local settle path, including rotation, trust, and grants to current holders.
        // A person with no ReplyTo reads the workspace key, just as in LocalTools.SettleAsync.
        var key = await recipient(task.ReplyTo as RoleTarget, ct).ConfigureAwait(false);
        var attachments = new AttachmentClient(http, links, workspace, Directory.CreateDirectory(Path.Combine(run.Root, "tmp")).FullName);
        var (attachmentRef, bundle) = await attachments.SendGitBundleAsync(run.WorkTree, target, key, branch, run.FullBundleFriction is null ? baseCommit : "", task.ThreadId, ct).ConfigureAwait(false);
        if (run.VerifiedReturnRevision is { } verified && bundle.HeadCommit != verified) return false;
        var body = new MessageBody
        {
            Friction = run.FullBundleFriction is { } note ? [note] : [],
            Summary = run.VerifiedReturnRevision is null
                ? "The runner returned the task's commits before cleaning up its worktree."
                : "The runner returned the exact commit used for required verification.",
        };
        var sealedBody = BodyCrypto.Seal(body, key);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, ApiRoutes.Mcp.TrimStart('/')),
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string> { [MbusHeaders.Agent] = delivery.AgentId },
        }, http, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
        var input = new SettleInput
        {
            ThreadId = task.ThreadId, DeliveryId = delivery.DeliveryId, ReplyOnly = true,
            Attempt = await TargetCapabilities.SupportsAsync(http, "settleAttemptV1", ct).ConfigureAwait(false) ? delivery.Attempt : null,
            Result = new SettleResult
            {
                Status = SettleStatus.Succeeded, Summary = body.Summary,
                FrictionKinds = [.. body.Friction.Select(note => note.Kind)],
                Refs = [new Ref { Kind = "git.commit", Sha = head }],
            },
            SealedReply = sealedBody.ToSealedBody(),
            Attachments = [attachmentRef],
        };
        var json = JsonSerializer.SerializeToElement(input, MessageBusJson.Options);
        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = McpToolNames.Settle,
            Arguments = json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        }, ct).ConfigureAwait(false);
        McpToolCatalog.ReadResult<SettleOutput>(result);
        return true;
    }
}
