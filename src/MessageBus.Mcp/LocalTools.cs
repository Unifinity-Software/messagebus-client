using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using MessageBus.Attachments.Client;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Projects;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MessageBus.Mcp;

/// <summary>
/// The local server's tools: each call is forwarded to the service's <c>/mcp</c> endpoint, with bodies,
/// replies and attachments sealed for their recipient first and what comes back opened with this
/// machine's keys. Runner-started agents (the original issue) get their task from <c>MBUS_DELIVERY_FILE</c> and settle
/// into <c>MBUS_SETTLE_FILE</c>; the runner reports the outcome after the agent exits.
/// </summary>
internal sealed partial class LocalTools : IAsyncDisposable
{
    private readonly LocalServerOptions _o;
    private readonly string _ws;
    private readonly MachineKeys _keys;
    private readonly AttachmentClient _attachments;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private readonly ConcurrentDictionary<string, MessageEnvelope> _tasks = new(StringComparer.Ordinal);
    private McpClient? _service;
    private string? _agent;
    private string? _clientKind;
    private string? _sessionProject;
    private string? _usageSessionId;
    private TaskDelivery? _delivery;
    private bool _deliveryRead;
    // Threads this session started: a runner task's scope (the original issue) lets it follow its own sub-tasks.
    private readonly ConcurrentDictionary<string, bool> _started = new(StringComparer.Ordinal);
    // the original issue: this session's inbox once open_inbox opened it (or the runner resumed this session for it).
    private readonly SessionRecords _sessions;
    private SessionRecord? _session;
    private string? _coordinationInboxId;
    private bool _coordinationAcquired;
    private DateTimeOffset _nextSessionPrune;
    private readonly SemaphoreSlim _coordinationInboxGate = new(1, 1);
    private readonly SemaphoreSlim _runnerSettleGate = new(1, 1);
    // the original issue: what this machine sent to each target, so the next bundle carries only new commits.
    private readonly SentCode _sentCode;
    // the original issue: attachment id to fetched commit, persisted beside sent-code.json.
    private readonly SentCode _fetchedCode;

    public LocalTools(LocalServerOptions options)
    {
        _o = options;
        _ws = options.WorkspaceId!;
        _keys = new MachineKeys(options.Http, _ws, options.MachineId, options.ConfigDirectory, options.Log, options.Time, options.MachineKeyPem);
        _attachments = new AttachmentClient(options.Http, options.Links!, _ws, options.TempDirectory);
        _sessions = new SessionRecords(options.ConfigDirectory);
        _session = options.Session;
        _sentCode = new SentCode(options.ConfigDirectory, options.Time);
        _fetchedCode = new SentCode(options.ConfigDirectory, options.Time, "fetched-code.json");
    }

    public async Task<CallToolResult> CallAsync(string? clientName, string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        var result = await CallCoreAsync(clientName, name, arguments, ct);
        try
        {
            // Reporting is best effort: it must never change the tool's result.
            if (result.IsError == true && result.StructuredContent is { } content
                && content.Deserialize<McpError>(MessageBusJson.Options) is { Retryable: false } error)
                _o.Refused?.Invoke(name, error);
        }
        catch (Exception) { }
        return result;
    }

    private Task<CallToolResult> CallCoreAsync(string? clientName, string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        // The runner's task scope (the original issue): only its tools and threads; git inside its sandbox.
        if (_o.Scope?.Check(name, arguments, new HashSet<string>(_started.Keys, StringComparer.Ordinal)) is { } refused)
            return Task.FromResult(McpToolCatalog.Error(refused));
        return GuardAsync(name, async () =>
        {
            using var git = Git.PrepareWith(_o.PrepareGit);
            _clientKind ??= AgentKindOf(clientName);
            _agent ??= _o.Agent ?? Delivery?.AgentId ?? _clientKind;
            return name switch
            {
                McpToolNames.Send => Ok(await SendAsync(Read<SendInput>(arguments), ct)),
                McpToolNames.SendMany => Ok(await SendManyAsync(Read<SendManyInput>(arguments), ct)),
                McpToolNames.Wait => Ok(await WaitAsync(Read<WaitInput>(arguments), ct)),
                McpToolNames.Watch => Ok(await WatchAsync(Read<WatchInput>(arguments), ct)),
                McpToolNames.Inbox => Ok(await InboxAsync(Read<InboxInput>(arguments), ct)),
                McpToolNames.Progress => Ok(await ProgressAsync(Read<ProgressInput>(arguments), ct)),
                McpToolNames.Settle => Ok(await SettleAsync(Read<SettleInput>(arguments), ct)),
                McpToolNames.EnsureProject => Ok(await EnsureProjectAsync(Read<EnsureProjectInput>(arguments), ct)),
                McpToolNames.FetchAttachment => Ok(await FetchAttachmentAsync(Read<FetchAttachmentInput>(arguments), ct)),
                McpToolNames.Retry => Ok(await RetryAsync(Read<RetryInput>(arguments), ct)),
                McpToolNames.CompleteProject => Ok(await CompleteProjectAsync(Read<CompleteProjectInput>(arguments), ct)),
                McpToolNames.RequestApproval => Ok(await RequestApprovalAsync(Read<RequestApprovalInput>(arguments), ct)),
                McpToolNames.WaitApproval => Ok(await WaitApprovalAsync(Read<WaitApprovalInput>(arguments), ct)),
                McpToolNames.OpenInbox => Ok(await OpenInboxAsync(Read<OpenInboxInput>(arguments), ct)),
                McpToolNames.Status => Ok(await StatusAsync(Read<StatusInput>(arguments), ct)),
                McpToolNames.Directory => await DirectoryAsync(Read<DirectoryInput>(arguments), ct),
                McpToolNames.Move or McpToolNames.FindProject or McpToolNames.Cancel
                    or McpToolNames.DescribeRole or McpToolNames.Subscribe => await PassThroughAsync(name, arguments, ct),
                _ => McpToolCatalog.Error(new McpError { Code = McpErrorCodes.NotFound, Message = $"There's no tool named {name}." }),
            };
        });
    }

    /// <summary>Runs one call, turning what can go wrong on this machine or on the way into the tool's error codes.</summary>
    private async Task<CallToolResult> GuardAsync(string name, Func<Task<CallToolResult>> call)
    {
        try
        {
            return await call();
        }
        catch (McpToolException e)
        {
            if (name is McpToolNames.Inbox or McpToolNames.Settle && _o.SettleFile is { } settleFile
                && e.Error.Code == McpErrorCodes.Forbidden && e.Error.Field?.StartsWith(RoleKeyReadiness.FieldPrefix, StringComparison.Ordinal) == true)
            {
                try
                {
                    await File.WriteAllTextAsync(RoleKeyReadiness.FileName(settleFile), e.Error.Field[RoleKeyReadiness.FieldPrefix.Length..]);
                }
                catch (Exception hintError) when (hintError is IOException or UnauthorizedAccessException)
                {
                    // This hint is best effort; preserve the actual key-refused error.
                }
            }
            return McpToolCatalog.Error(e.Error);
        }
        catch (AttachmentTransferException e)
        {
            return McpToolCatalog.Error(new McpError
            {
                Code = e.Code ?? (e.Status == HttpStatusCode.NotFound ? McpErrorCodes.NotFound : McpErrorCodes.Unavailable),
                Message = "The attachment transfer failed: " + Reason(e), Retryable = e.Status is null or >= HttpStatusCode.InternalServerError,
            });
        }
        catch (GitException e)
        {
            return McpToolCatalog.Error(await TranslateGitAsync(e));
        }
        catch (MissingKeyException e)
        {
            return McpToolCatalog.Error(new McpError
            {
                Code = McpErrorCodes.Forbidden,
                Message = $"This machine can't decrypt it: it holds no grant for key {e.KeyId}. A workspace admin's mbus grants role keys when it sends to the role; ask one to send to it, then try again.",
            });
        }
        catch (HttpRequestException e)
        {
            await ResetServiceAsync();
            return McpToolCatalog.Error(new McpError { Code = McpErrorCodes.Unavailable, Message = "Couldn't reach messagebus.ai: " + Reason(e), Retryable = true, RetryAfterSeconds = 5 });
        }
        catch (McpException e)
        {
            await ResetServiceAsync();
            return McpToolCatalog.Error(new McpError { Code = McpErrorCodes.Unavailable, Message = "messagebus.ai's MCP endpoint failed: " + Reason(e), Retryable = true, RetryAfterSeconds = 5 });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await _o.Log.WriteLineAsync($"mbus mcp: {name} failed: {e}");
            return McpToolCatalog.Error(new McpError { Code = McpErrorCodes.Internal, Message = $"{name} failed on this machine: {Reason(e)}" });
        }
    }

    /// <summary>
    /// Why a call failed, as one short line for the agent (the full exception goes to the log only): the innermost
    /// exception's message, which names the actual cause, else the outer one's, else the exception's type. Never empty.
    /// </summary>
    internal static string Reason(Exception e)
    {
        var innermost = e;
        while (innermost.InnerException is { } inner) innermost = inner;
        var text = new[] { innermost.Message, e.Message }.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        if (text is null) return innermost.GetType().Name;
        text = text.Trim().Split('\n', 2)[0].Trim();
        return text.Length <= 300 ? text : text[..299] + "…";
    }

    private static CallToolResult Ok<T>(T value) => McpToolCatalog.Ok(value);

    private static T Read<T>(IDictionary<string, JsonElement>? arguments) => McpToolCatalog.ReadInput<T>(arguments);

    /// <summary>The MCP client's name as one of the machine's agent kinds (the service picks that agent).</summary>
    internal static string? AgentKindOf(string? clientName) => clientName?.ToLowerInvariant() switch
    {
        null => null,
        var n when n.Contains("claude") => "claude-code",
        var n when n.Contains("codex") => "codex",
        var n when n.Contains("copilot") => "copilot",
        var n when n.Contains("opencode") => "opencode",
        _ => null,
    };

    // ---- the service ----

    private async Task<McpClient> ServiceAsync(CancellationToken ct)
    {
        if (_service is { } ready) return ready;
        await _connect.WaitAsync(ct);
        try
        {
            if (_service is not null) return _service;
            var headers = new Dictionary<string, string>();
            if (_agent is not null) headers[MbusHeaders.Agent] = _agent;
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_o.Http.BaseAddress!, ApiRoutes.Mcp.TrimStart('/')),
                Name = "messagebus.ai",
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
                AdditionalHeaders = headers,
            }, _o.Http, ownsHttpClient: false);
            return _service = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "mbus", Version = _o.ClientVersion },
            }, cancellationToken: ct);
        }
        finally
        {
            _connect.Release();
        }
    }

    private async Task ResetServiceAsync()
    {
        var old = Interlocked.Exchange(ref _service, null);
        if (old is not null) await old.DisposeAsync();
    }

    private async Task<CallToolResult> CallServiceAsync(string name, IDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        var client = await ServiceAsync(ct);
        return await client.CallToolAsync(new CallToolRequestParams { Name = name, Arguments = arguments }, ct);
    }

    private Task<CallToolResult> CallServiceAsync(string name, object? input, JsonTypeInfo type, CancellationToken ct)
    {
        var element = JsonSerializer.SerializeToElement(input, type);
        var arguments = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        return CallServiceAsync(name, arguments, ct);
    }

    private async Task<TOut> ForwardAsync<TIn, TOut>(string name, TIn input, CancellationToken ct) =>
        McpToolCatalog.ReadResult<TOut>(await CallServiceAsync(name, input, MessageBusJson.Options.GetTypeInfo(typeof(TIn)), ct));

    private Task<CallToolResult> PassThroughAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        // Validated here first (same parsing as the service), so a bad call never leaves the machine.
        var spec = McpToolCatalog.Find(name)!;
        var input = McpToolCatalog.ReadInput(spec.Input, arguments);
        return CallServiceAsync(name, input, MessageBusJson.Options.GetTypeInfo(spec.Input), ct);
    }

    private async Task<CallToolResult> DirectoryAsync(DirectoryInput input, CancellationToken ct)
    {
        const string prefix = "github:";
        var repositoryId = input.VerificationRepositoryId;
        if (repositoryId is not null
            && (!repositoryId.StartsWith(prefix, StringComparison.Ordinal)
                || repositoryId.Length == prefix.Length
                || repositoryId[prefix.Length] == '0'
                || repositoryId.AsSpan(prefix.Length).ContainsAnyExceptInRange('0', '9')))
            throw McpToolException.Invalid("verificationRepositoryId", "Use the trusted GitHub repository's numeric ID in github:<numeric-id> form.");

        // Only the role filters go to the hosted MCP endpoint.
        var directory = await CallServiceAsync(McpToolNames.Directory, input with { VerificationRepositoryId = null },
            MessageBusJson.Options.GetTypeInfo(typeof(DirectoryInput)), ct);
        if (directory.IsError == true || repositoryId is null) return directory;

        var route = ApiRoutes.VerificationEnvironments.Replace("{workspaceId}", Uri.EscapeDataString(_ws), StringComparison.Ordinal);
        using var response = await _o.Http.GetAsync($"{route.TrimStart('/')}?repositoryId={Uri.EscapeDataString(repositoryId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return directory;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw McpToolException.Forbidden("The session is not authorized to discover verification environments for this repository.");
        if (!response.IsSuccessStatusCode)
            throw McpToolException.Unavailable($"Couldn't discover verification environments ({(int)response.StatusCode}).");

        var verification = await response.Content.ReadFromJsonAsync(
            (JsonTypeInfo<VerificationDirectory>)MessageBusJson.Options.GetTypeInfo(typeof(VerificationDirectory)), ct)
            ?? throw McpToolException.Unavailable("The verification registry returned an empty response.");
        return Ok(McpToolCatalog.ReadResult<DirectoryResult>(directory) with { Verification = verification });
    }

    // ---- the runner's task ----

    /// <summary>
    /// The task the runner started this agent for, when it did: the runner's own copy when it serves the run's tools
    /// (the delivery file is in the run's folder, where the agent can rewrite it), else the file.
    /// </summary>
    private TaskDelivery? Delivery
    {
        get
        {
            if (_o.Delivery is { } given) return given;
            if (_deliveryRead) return _delivery;
            _deliveryRead = true;
            if (_o.DeliveryFile is { } file && File.Exists(file))
                _delivery = MessageBusJson.Deserialize<TaskDelivery>(File.ReadAllText(file));
            return _delivery;
        }
    }

    /// <summary>
    /// A path the agent gave (relative to the working directory; the working directory when empty). For a runner's task (the original issue)
    /// it must stay in the run's folder: the runner reads and writes it as this machine, outside the task's sandbox.
    /// </summary>
    private string LocalPath(string? given, string field)
    {
        var full = given is { Length: > 0 } ? Path.GetFullPath(given, _o.WorkingDirectory) : _o.WorkingDirectory;
        if (_o.Scope is { } scope && !scope.Contains(full))
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.Forbidden, Field = field,
                Message = $"{full} is outside this task's run folder ({scope.Root}); the mbus tools read and write only inside it.",
            });
        return full;
    }

    /// <summary>Whether the call names the runner's task: by the runner's scope when it serves the run's tools, else the delivery.</summary>
    private bool IsRunnerTask(string threadId, string? deliveryId) =>
        Delivery is { } d && RunnerThreadId(d) == threadId && (deliveryId is null || deliveryId == RunnerDeliveryId(d));

    private string RunnerThreadId(TaskDelivery d) => _o.Scope?.ThreadId ?? d.Envelope.ThreadId;

    private string RunnerDeliveryId(TaskDelivery d) => _o.Scope?.DeliveryId ?? d.DeliveryId;

    // ---- sending ----

    private async Task<SendResult> SendAsync(SendInput input, CancellationToken ct)
    {
        if (input.Verification is not null)
            await RequireVerifiedSendAsync(McpToolNames.SendVerified, ct);
        var bundles = new List<SentBundle>();
        var tool = input.Verification is null ? McpToolNames.Send : McpToolNames.SendVerified;
        var sent = await ForwardAsync<SendInput, SendResult>(tool, await PrepareAsync(WithSessionReplyTo(input), null, ct, bundles), ct);
        _started[sent.ThreadId] = true;
        Remember(sent.ThreadId, bundles);
        return WithProjectNotice(sent with { Notice = JoinNotices(sent.Notice, bundles) }, input);
    }

    private async Task<SendManyResult> SendManyAsync(SendManyInput input, CancellationToken ct)
    {
        if (input.Messages is not { Count: > 0 }) throw McpToolException.Invalid("messages", "Send between 1 and 100 messages.");
        if (input.Messages.Any(message => message.Verification is not null))
            await RequireVerifiedSendAsync(McpToolNames.SendManyVerified, ct);
        var project = input.ProjectId;
        var prepared = new List<SendInput>();
        var parents = new Dictionary<string, StatusResult>(StringComparer.Ordinal);
        var bundles = new List<List<SentBundle>>();
        foreach (var (message, i) in input.Messages.Select((m, i) => (m, i)))
        {
            bundles.Add([]);
            prepared.Add(await PrepareAsync(WithSessionReplyTo(message with { ProjectId = message.ProjectId ?? project }), $"messages[{i}].", ct, bundles[i], parents));
        }
        var tool = prepared.Any(message => message.Verification is not null) ? McpToolNames.SendManyVerified : McpToolNames.SendMany;
        var sent = await ForwardAsync<SendManyInput, SendManyResult>(tool,
            input with { Messages = prepared, ProjectId = project ?? DefaultProject() }, ct);
        foreach (var result in sent.Results) _started[result.ThreadId] = true;
        foreach (var (result, i) in sent.Results.Select((r, i) => (r, i)).Where(r => r.i < bundles.Count)) Remember(result.ThreadId, bundles[i]);
        if (sent.JoinThreadId is { } join) _started[join] = true;
        var results = sent.Results.Select((result, i) =>
            WithProjectNotice(i < bundles.Count ? result with { Notice = JoinNotices(result.Notice, bundles[i]) } : result, input.Messages[i])).ToArray();
        var unassigned = sent.Results.Where((result, i) => NeedsProjectNotice(result, input.Messages[i])).Count();
        var notice = $"{unassigned} of {sent.Results.Count} tasks were sent without a project. To put them in one, call move with each threadId and a projectId. For later sends, pass projectId or call ensure_project first.";
        return sent with
        {
            Results = results,
            Notice = unassigned > 0
                ? (string.IsNullOrEmpty(sent.Notice) ? notice : sent.Notice + " " + notice)
                : sent.Notice,
        };
    }

    private async Task RequireVerifiedSendAsync(string tool, CancellationToken ct)
    {
        // A listed tool alone is not negotiation: older services can share the catalog while
        // lacking required-task admission. Check the live registry before packaging any child.
        var route = ApiRoutes.VerificationEnvironments.Replace("{workspaceId}", Uri.EscapeDataString(_ws), StringComparison.Ordinal);
        using var response = await _o.Http.GetAsync(route.TrimStart('/'), ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw McpToolException.Forbidden("The session is not authorized to request required verification.");
        if (!response.IsSuccessStatusCode)
            throw McpToolException.Unavailable("The service has not negotiated required verification requests.");
        VerificationDirectory? registry;
        try
        {
            registry = await response.Content.ReadFromJsonAsync(
                (JsonTypeInfo<VerificationDirectory>)MessageBusJson.Options.GetTypeInfo(typeof(VerificationDirectory)), ct);
        }
        catch (JsonException)
        {
            throw McpToolException.Unavailable("The verification registry returned an invalid response.");
        }
        if (registry is not { RegistryVersion: 1, RequiredTaskRequestsSupported: true, Recipes: not null, Environments: not null })
            throw McpToolException.Unavailable("The service does not support required verification requests in registry version 1.");

        var listed = await (await ServiceAsync(ct)).ListToolsAsync(cancellationToken: ct);
        if (!listed.Any(candidate => candidate.Name == tool))
            throw McpToolException.Unavailable("The service has not installed the required verification send handler.");
    }

    private static string? JoinNotices(string? notice, IReadOnlyList<SentBundle> bundles)
    {
        var notices = new[] { notice }.Concat(bundles.Select(b => b.Notice)).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
        return notices.Count == 0 ? null : string.Join(" ", notices);
    }

    /// <summary>
    /// Checks for secrets, seals the body and packages attachments for the target's key. Tracks local Git bundles
    /// in <paramref name="bundles"/>. Only the first Git bundle in the final list can use the last sent commit.
    /// </summary>
    private async Task<SendInput> PrepareAsync(SendInput input, string? at, CancellationToken ct, List<SentBundle>? bundles = null, Dictionary<string, StatusResult>? parents = null)
    {
        at ??= "";
        if (input.To is null) throw McpToolException.Invalid(at + "to", "Say who the message is for, for example role:developer.");
        if (input.Body is not { } body) throw McpToolException.Invalid(at + "body", "A message needs a body: {summary, details?, data?}.");
        if (input.Verification is { } verification)
        {
            if (input.To is not RoleTarget || input.Type?.StartsWith("task.", StringComparison.Ordinal) != true)
                throw McpToolException.Invalid(at + "verification", "Required verification is only available for role-targeted tasks.");
            if (string.IsNullOrWhiteSpace(verification.Environment))
                throw McpToolException.Invalid(at + "verification.environment", "Name a workspace-approved environment.");
            if (verification.RequiredChecks is not { Count: > 0 }
                || verification.RequiredChecks.Any(check => check is not ("test" or "typecheck" or "build" or "browser")))
                throw McpToolException.Invalid(at + "verification.requiredChecks", "Name one or more checks: test, typecheck, build or browser.");
        }
        Scan(body, at + "body");

        var key = await RecipientAsync(input.To, ct);
        // Answers come back sealed to the workspace key: make sure it exists and every machine holds it.
        if (input.To is RoleTarget) await EnsureWorkspaceKeyAsync(ct);

        var attachments = new List<AttachmentRef>(input.Attachments ?? []);
        var hasBundle = attachments.Any(a => a.Format == AttachmentFormat.GitBundle);
        foreach (var (spec, i) in (input.Attach ?? []).Select((s, i) => (s, i)))
        {
            attachments.Add(await PackageAsync(spec, input.To, key, input.ThreadId, $"{at}attach[{i}]", ct, bundles, primary: !hasBundle));
            hasBundle |= spec.Format == AttachmentFormat.GitBundle;
        }

        if (input.ParentThreadId is { } parent && bundles is { Count: > 0 })
            await AddParentBundleNoticesAsync(parent, bundles, parents ?? new Dictionary<string, StatusResult>(StringComparer.Ordinal), ct);

        return input with
        {
            ProjectId = input.ProjectId ?? DefaultProject(),
            Body = null,
            SealedBody = Seal(body, key, at + "body"),
            Attach = [],
            Attachments = attachments,
        };
    }

    /// <summary>The key to seal to; the key service on this machine also grants it to the machines the service says should hold it.</summary>
    private Task<PublicKeyInfo> RecipientAsync(MessageTarget? to, CancellationToken ct) => _keys.RecipientAsync(to, ct);

    private async Task EnsureWorkspaceKeyAsync(CancellationToken ct)
    {
        try
        {
            await RecipientAsync(null, ct);
        }
        catch (McpToolException e) when (e.Error.Code == McpErrorCodes.Forbidden)
        {
            // Not an admin and no workspace key yet: the task still goes; whoever answers makes or needs it.
            await _o.Log.WriteLineAsync("mbus mcp: " + e.Message);
        }
    }

    private static void Scan(MessageBody body, string field)
    {
        var findings = SecretScanner.Scan(body);
        if (findings.Count > 0)
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.InvalidInput, Field = field,
                Message = $"The {field} looks like it holds a secret ({string.Join(", ", findings.Select(f => f.Kind).Distinct())}). " +
                          "Take it out and send again; never pass credentials in messages.",
            });
    }

    private static SealedBody Seal(MessageBody body, PublicKeyInfo key, string field)
    {
        SealedBytes sealedBytes;
        try
        {
            sealedBytes = BodyCrypto.Seal(body, key);
        }
        catch (ArgumentException e)
        {
            throw new McpToolException(new McpError { Code = McpErrorCodes.TooLarge, Field = field, Message = e.Message });
        }
        // Inline up to the body limit: the service moves anything over 64 KB to blob storage itself.
        return new SealedBody
        {
            Body = new BodyRef
            {
                SizeBytes = sealedBytes.Ciphertext.Length, Sha256 = sealedBytes.Sha256, ContentType = MessageLimits.BodyContentType,
                Data = Convert.ToBase64String(sealedBytes.Ciphertext),
            },
            Encryption = sealedBytes.Encryption,
        };
    }

    private async Task<McpError> TranslateGitAsync(GitException e, string? attachment = null)
    {
        string? field = e.Input is { } input ? attachment is null ? input : attachment + "." + input : null;
        var message = e.Message;
        if (e.RawOutput)
        {
            await _o.Log.WriteLineAsync(e.Message);
            message = e.StandardError ?? "";
            if (message.Contains("not a git repository", StringComparison.OrdinalIgnoreCase))
            {
                message = "That folder isn't a git repository.";
                field = attachment is null ? "repository" : attachment + ".path";
            }
            else if (message.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) || message.Contains("Needed a single revision", StringComparison.OrdinalIgnoreCase) || message.Contains("bad revision", StringComparison.OrdinalIgnoreCase))
                message = "The branch or commit wasn't found in this repository.";
            else if (message.Contains("empty bundle", StringComparison.OrdinalIgnoreCase))
                message = "The branch has nothing after that base. If the fetched copy is newer, send branch: mbus/<branch>.";
            else if (message.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
                message = "Git refused this repository's ownership. Add its trusted path with git config --global --add safe.directory <path>.";
            else message = "git couldn't do that: " + Reason(new Exception(message));
        }
        return new McpError { Code = McpErrorCodes.InvalidInput, Message = message, Field = field };
    }

    private async Task<AttachmentRef> PackageAsync(AttachSpec spec, MessageTarget to, PublicKeyInfo key, string? threadId, string field, CancellationToken ct,
        List<SentBundle>? bundles = null, bool primary = false)
    {
        if (spec.ExpectedHead is { } expectedHead)
        {
            if (spec.Format != AttachmentFormat.GitBundle)
                throw McpToolException.Invalid(field + ".expectedHead", "Only git bundles support expectedHead.");
            if (expectedHead.Length is not (40 or 64) || !expectedHead.All(Uri.IsHexDigit))
                throw McpToolException.Invalid(field + ".expectedHead", "Give a full 40- or 64-digit hex commit SHA.");
        }
        var path = LocalPath(spec.Path, field + ".path");
        if (spec.Format != AttachmentFormat.File && _o.Scope?.RepositoryProblem(path) is { } problem)
            throw new McpToolException(new McpError { Code = McpErrorCodes.Forbidden, Field = field + ".path", Message = $"This repository can't be sent: {problem}." });
        try
        {
            if (spec.Format != AttachmentFormat.File)
            {
                try { spec = spec with { Branch = await GitBundles.ResolveBranchAsync(path, spec.Branch, ct) }; }
                catch (GitException e) { throw new McpToolException(await TranslateGitAsync(e, field)); }
                if (!string.IsNullOrEmpty(spec.Base))
                {
                    try { await Git.CommitIdAsync(path, spec.Base, ct); }
                    catch (GitException e) when (e.RawOutput)
                    {
                        await _o.Log.WriteLineAsync(e.Message);
                        throw McpToolException.Invalid(field + ".base", "Give a base commit that exists in this repository, or an empty base for the whole history.");
                    }
                }
            }
            switch (spec.Format)
            {
                case AttachmentFormat.GitBundle when bundles is not null:
                    return await PackageTaskBundleAsync(spec, path, to, key, threadId, bundles, primary, ct);
                case AttachmentFormat.GitBundle:
                    return (await _attachments.SendGitBundleAsync(path, to, key, spec.Branch, spec.Base, threadId, spec.ExpectedHead, ct)).Attachment;
                case AttachmentFormat.Patch:
                {
                    if (spec.Base is not { Length: > 0 } baseCommit) throw McpToolException.Invalid(field + ".base", "A patch needs base: the commit it starts after.");
                    var dir = TempFolders.Create(_o.TempDirectory, "mbus-patch-");
                    try
                    {
                        var patch = await GitBundles.CreatePatchAsync(path, Path.Combine(dir.FullName, "changes.patch"), baseCommit, spec.Branch, ct);
                        await using var file = File.OpenRead(patch.Path);
                        return await _attachments.UploadAsync(file, new AttachmentUpload(to, patch.Branch + ".patch", AttachmentFormat.Patch, threadId,
                            GitBundles.PatchContentType, patch.Branch, patch.BaseCommit), key, ct);
                    }
                    finally
                    {
                        dir.Delete(recursive: true);
                    }
                }
                default:
                {
                    if (spec.Path is null || !File.Exists(path)) throw McpToolException.Invalid(field + ".path", "Give the path of the file to attach.");
                    await using var file = File.OpenRead(path);
                    var name = Path.GetFileName(path);
                    return await _attachments.UploadAsync(file, new AttachmentUpload(to, name, AttachmentFormat.File, threadId, MediaTypes.ForFileName(name)), key, ct);
                }
            }
        }
        catch (GitException e)
        {
            throw new McpToolException(await TranslateGitAsync(e, field));
        }
    }

    /// <summary>
    /// Sends a finished thread's task again (the original issue): its first message opened with this machine's keys, the
    /// note put first in the details, sealed again for the target's current key, its git bundles and patches
    /// packaged again from their branches, and the next round of its dedupe key (so a second retry of the
    /// same thread returns the first one).
    /// </summary>
    private async Task<SendResult> RetryAsync(RetryInput input, CancellationToken ct)
    {
        var thread = (await ForwardAsync<StatusInput, StatusResult>(McpToolNames.Status,
                         new StatusInput { ThreadIds = [input.ThreadId], Compact = false }, ct)).Threads.FirstOrDefault()
                     ?? throw McpToolException.NotFound($"Thread {input.ThreadId} wasn't found.");
        if (thread.Status is ThreadStatus.Queued or ThreadStatus.Delivered or ThreadStatus.Working or ThreadStatus.WaitingOnJoin)
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.Conflict, Field = "threadId",
                Message = $"Thread {input.ThreadId} is still {MessageBusJson.Serialize(thread.Status).Trim('"')}: wait for it to finish, or cancel it, before sending it again.",
            });
        var task = (await ThreadMessagesAsync(input.ThreadId, ct)).FirstOrDefault(m => m.Id == thread.RootMessageId)
                   ?? throw McpToolException.NotFound($"Thread {input.ThreadId}'s task message wasn't found.");
        if (task.Attachments.Count(a => a.Format is AttachmentFormat.GitBundle or AttachmentFormat.Patch) > 1)
            throw McpToolException.Invalid("repository", "Retry cannot identify the original repositories for multiple code attachments. Use a fresh send with an explicit path and branch per code attachment and expectedHead for each Git bundle.");
        if (thread.Verification is not null && task.Verification is null)
            throw McpToolException.Invalid("threadId", "The original task's verification requirements are unavailable.");
        if (task.Body.Data is not { } data)
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.TooLarge, Field = "threadId",
                Message = "The task's body is stored on its own (over 64 KB), so retry can't read it back. Send the task again with send.",
            });
        var bytes = Convert.FromBase64String(data);
        var body = task.Encryption is { } encryption
            ? await _keys.OpenAsync(bytes, encryption, ct)
            : MessageBusJson.Deserialize<MessageBody>(Encoding.UTF8.GetString(bytes))
              ?? throw McpToolException.Invalid("threadId", "The task's body isn't a message body.");

        var (dedupeKey, round) = NextRound(task.DedupeKey, input.ThreadId);
        if (input.Note is { Length: > 0 } note)
            body = body with { Details = $"**Retry note (round {round}):** {note.Trim()}" + (body.Details is { Length: > 0 } d ? "\n\n" + d : "") };

        var repository = LocalPath(input.Repository, "repository");
        var attach = new List<AttachSpec>();
        var keep = new List<AttachmentRef>();
        var fallbackNotices = new List<string>();
        foreach (var a in task.Attachments)
        {
            if (a.Format is AttachmentFormat.GitBundle or AttachmentFormat.Patch && a.Branch is { Length: > 0 } branch)
            {
                var source = $"refs/heads/{branch}";
                try
                {
                    try { await Git.CommitIdAsync(repository, $"refs/heads/{branch}", ct); }
                    catch (GitException)
                    {
                        source = await GitBundles.ResolveBranchAsync(repository, $"mbus/{branch}", ct);
                        fallbackNotices.Add($"Sent {source} because local {branch} doesn't exist");
                    }
                }
                catch (GitException) { } // PackageAsync reports the repository or branch error with its input field.
                attach.Add(new AttachSpec { Format = a.Format, Path = repository, Branch = source, Base = a.BaseCommit ?? "" });
            }
            else
                keep.Add(a); // a file can't be made again here; it goes as it was
        }
        var labels = new Dictionary<string, string>(task.Labels, StringComparer.Ordinal)
        {
            ["round"] = round.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["retryOf"] = input.ThreadId,
        };
        try
        {
            var sent = await SendAsync(new SendInput
            {
                To = task.Target, Type = task.Type, Title = RoundTitle(task.Title, round), Body = body,
                SourceThreadId = input.ThreadId, Verification = task.Verification,
                ProjectId = task.ProjectId, ParentThreadId = thread.ParentThreadId, Expect = task.Expect, ReplyTo = task.ReplyTo,
                Priority = task.Priority, DedupeKey = dedupeKey, Labels = labels, Refs = task.Refs,
                Attach = attach, Attachments = keep, ExcludeAgents = task.ExcludeAgents, Model = task.Model,
            }, ct);
            return fallbackNotices.Count == 0 ? sent : sent with { Notice = string.Join(" ", new[] { sent.Notice }.Concat(fallbackNotices).Where(n => !string.IsNullOrEmpty(n))) };
        }
        catch (McpToolException e) when (e.Error.Code == McpErrorCodes.InvalidInput && e.Error.Field?.StartsWith("attach[", StringComparison.Ordinal) == true)
        {
            throw McpToolException.Invalid("repository",
                $"Couldn't package the task's branches ({string.Join(", ", attach.Select(a => a.Branch))}) again from {repository}. " +
                "Pass repository: the repository the task's code came from. " + e.Message);
        }
    }

    /// <summary>The next round of a dedupe key: a trailing number after a separator goes up by one (<c>…/T2/1</c> → <c>…/T2/2</c>), else <c>/2</c> is added.</summary>
    internal static (string Key, int Round) NextRound(string? dedupeKey, string threadId)
    {
        if (dedupeKey is { Length: > 0 } && RoundSuffix().Match(dedupeKey) is { Success: true } m
            && int.TryParse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return (m.Groups["head"].Value + (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), n + 1);
        return ((dedupeKey is { Length: > 0 } ? dedupeKey : $"retry:{threadId}") + "/2", 2);
    }

    private static string? RoundTitle(string? title, int round)
    {
        if (title is null) return null;
        var text = $"{RoundTitleSuffix().Replace(title, "")} (round {round})";
        return text.Length <= MessageLimits.MaxTitleLength ? text : text[..MessageLimits.MaxTitleLength];
    }

    [GeneratedRegex(@"^(?<head>.*[/:#._-]r?)(?<n>\d{1,6})$")]
    private static partial Regex RoundSuffix();

    [GeneratedRegex(@" \(round \d+\)$")]
    private static partial Regex RoundTitleSuffix();

    // ---- projects ----

    /// <summary>
    /// Completes the project. Without a usage from the agent, the orchestrating session's own comes from its client's
    /// session log for this directory, less the persisted usage already assigned to other projects. Explicit usage is unchanged.
    /// </summary>
    private async Task<CompleteProjectResult> CompleteProjectAsync(CompleteProjectInput input, CancellationToken ct)
    {
        if (input.Usage is not null || _clientKind is not ("claude-code" or "codex"))
            return await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, input, ct);

        var original = input;
        await _coordinationInboxGate.WaitAsync(ct);
        try
        {
            var now = _o.Time.GetUtcNow();
            var newest = AgentSessions.Newest(_clientKind, _o.WorkingDirectory, ClaudeHome, CodexHome, now);
            var sessionId = _usageSessionId ?? _session?.SessionId ?? newest;
            if (sessionId is null)
            {
                var omitted = await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, input, ct);
                return omitted with { Notice = "Session usage delta is unavailable; automatic orchestrator usage was omitted." };
            }
            _usageSessionId = sessionId;
            using var journal = await ConversationUsage.OpenAsync(_sessions.Directory, _clientKind, sessionId, _o.Time, ct);
            string? recoveryNotice = null;
            async Task<CompleteProjectResult> Report(CompleteProjectInput report, bool recovery = false)
            {
                try
                {
                    var result = await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, report, ct);
                    journal.Commit();
                    return result;
                }
                catch (McpToolException e) when (!e.Error.Retryable)
                {
                    if (recovery)
                    {
                        journal.Reserve();
                        recoveryNotice = "An earlier uncertain usage report was rejected; its usage interval remains reserved.";
                    }
                    else journal.Reject();
                    throw;
                }
            }
            // An uncertain response must be resolved with the exact same report before another project can claim usage.
            if (journal.Pending is { } pending)
            {
                try
                {
                    var retried = await Report(pending, recovery: true);
                    if (pending.ProjectId == input.ProjectId) return retried;
                }
                catch (McpToolException e) when (!e.Error.Retryable && pending.ProjectId != input.ProjectId) { }
            }
            if (journal.Report(input.ProjectId) is { } reported)
            {
                var repeated = await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, input with { Usage = reported }, ct);
                return repeated with { Notice = recoveryNotice ?? repeated.Notice };
            }
            var project = (await ForwardAsync<StatusInput, StatusResult>(McpToolNames.Status, new StatusInput { ProjectId = input.ProjectId }, ct)).Project;
            if (project is null || project.Status != MessageBus.Contracts.Projects.ProjectStatus.Active)
            {
                var completed = await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, input, ct);
                return completed with { Notice = recoveryNotice ?? completed.Notice };
            }
            Usage? Read(DateTimeOffset? before = null, DateTimeOffset? after = null) => _clientKind == "claude-code"
                ? SessionUsage.FromClaudeCode(ClaudeHome, _o.WorkingDirectory, sessionId, before, after)
                : SessionUsage.FromCodex(CodexHome, _o.WorkingDirectory, project.CreatedAt, now, sessionId, before, after);
            Usage? total = null, delta = null;
            string? notice = null;
            try
            {
                total = Read();
                var previous = journal.Total ?? new Usage();
                if (Read(project.CreatedAt) is { } created && ConversationUsage.AtLeast(created, previous))
                    previous = created;
                if (total is not null)
                {
                    delta = ConversationUsage.Delta(total, previous);
                    if (journal.Total is null && delta is not null)
                        delta = delta with { ActiveSeconds = Read(after: project.CreatedAt)?.ActiveSeconds ?? 0 };
                }
                if (total is null || (delta is null && (!ConversationUsage.SameCounts(total, previous) || (newest is not null && newest != sessionId))))
                    notice = "Session usage delta is unavailable or a newer conversation log exists; automatic orchestrator usage was omitted.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                notice = "Session log could not be read; automatic orchestrator usage was omitted.";
            }
            if (delta is null)
            {
                var omitted = await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, input, ct);
                return omitted with { Notice = string.Join(" ", new[] { omitted.Notice, recoveryNotice, notice }.Where(n => !string.IsNullOrEmpty(n))) is { Length: > 0 } message ? message : null };
            }
            input = input with { Usage = delta };
            journal.Prepare(input, total!);
            return (await Report(input)) with { Notice = recoveryNotice ?? notice };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            var omitted = await ForwardAsync<CompleteProjectInput, CompleteProjectResult>(McpToolNames.CompleteProject, original, ct);
            return omitted with { Notice = "Session usage journal is unavailable; automatic orchestrator usage was omitted." };
        }
        finally { _coordinationInboxGate.Release(); }
    }

    private async Task<EnsureProjectResult> EnsureProjectAsync(EnsureProjectInput input, CancellationToken ct)
    {
        if (input.ClaimedWorkItems is null)
            throw McpToolException.Invalid("claimedWorkItems", "Give a work-item list or omit it.");
        if (input.ClaimedWorkItems.Count > 0)
        {
            await _coordinationInboxGate.WaitAsync(ct);
            try
            {
                // Session-log "newest" inference is appropriate for late replies, but cannot establish
                // exclusive coordination: two live sessions may share a folder and account.
                _coordinationInboxId ??= _o.Session?.InboxId;
                if (_coordinationInboxId is null && input.CoordinatorInboxId is { } resumeId)
                {
                    var saved = _sessions.Find(resumeId);
                    if (saved is null || saved.WorkspaceId != _ws)
                        throw McpToolException.Invalid("coordinatorInboxId", "Resume an inbox saved by this machine in this workspace.");
                    _coordinationInboxId = resumeId;
                    _session = saved;
                }
                if (_coordinationInboxId is null)
                {
                    var opened = await OpenInboxCoreAsync(new(), ct);
                    _coordinationInboxId = opened.InboxId;
                }
                // Persist before the request: a lost response may still have acquired server claims.
                if (_session is { } owner)
                {
                    _session = (_sessions.Find(owner.InboxId) ?? owner) with { CoordinationRecovery = true };
                    _sessions.Save(_session);
                }
                input = input with { CoordinatorInboxId = _coordinationInboxId };
            }
            finally { _coordinationInboxGate.Release(); }
        }
        var response = await ForwardAsync<EnsureProjectInput, JsonElement>(McpToolNames.EnsureProject, input, ct);
        var result = response.Deserialize((JsonTypeInfo<EnsureProjectResult>)MessageBusJson.Options.GetTypeInfo(typeof(EnsureProjectResult)))
            ?? throw new McpToolException(new McpError { Code = McpErrorCodes.Internal, Message = "The service sent an empty result." });
        // An omitted scope deserializes as Workspace. Require an explicit acknowledgment on the wire.
        if (input.ExecutionScope is { } requested &&
            (!response.GetProperty("project").TryGetProperty("executionScope", out var scope) ||
             scope.ValueKind != JsonValueKind.String || result.Project.ExecutionScope != requested ||
             (requested == ProjectExecutionScope.OwnerRunners && string.IsNullOrWhiteSpace(result.Project.ExecutionOwnerSubjectId))))
            throw new McpToolException(new McpError { Code = McpErrorCodes.Internal, Message = "The service did not confirm the requested project execution scope and required owner." });
        if (input.ClaimedWorkItems.Count > 0 &&
            (!result.CoordinationAcquired || string.IsNullOrWhiteSpace(result.CoordinatorInboxId)))
            throw new McpToolException(new McpError { Code = McpErrorCodes.Internal, Message = "The service did not confirm acquisition of the requested claims with a coordinator inbox." });
        _sessionProject = result.Project.ProjectId;
        _coordinationAcquired |= result.CoordinationAcquired;
        return result;
    }

    /// <summary>The project this MCP session last ensured, or its assigned runner task's project.</summary>
    private string? DefaultProject() => _sessionProject ?? Delivery?.Envelope.ProjectId;

    private const string NoProjectNotice = "This task has no project. To put it in one, call move with its threadId and a projectId. For later sends, pass projectId or call ensure_project first.";

    private static bool NeedsProjectNotice(SendResult result, SendInput input) =>
        result.ProjectId is null && input.ThreadId is null && input.To is not InboxTarget;

    private static SendResult WithProjectNotice(SendResult result, SendInput input) => NeedsProjectNotice(result, input)
        ? result with { Notice = string.IsNullOrEmpty(result.Notice) ? NoProjectNotice : result.Notice + " " + NoProjectNotice }
        : result;

    // ---- receiving ----

    /// <summary>
    /// The longest one blocking call may take: MCP clients end tool calls after a fixed time (Claude Code at about
    /// 120 s, Codex at its <c>tool_timeout_sec</c>, 60 s by default), and a call they end loses its result (the original issue).
    /// Codex's is read from its config once (the original issue): Codex reads it when it starts, too.
    /// </summary>
    private int CallLimitSeconds => _o.MaxCallSeconds ?? _clientKind switch
    {
        "claude-code" => McpCallLimits.ClaudeCodeSeconds,
        "codex" => _codexCallLimit ??= CodexConfig.CallLimitSecondsOrDefault(_o.CodexHome),
        _ => McpCallLimits.OtherClientSeconds,
    };

    private int? _codexCallLimit;

    private DateTimeOffset Deadline(int timeoutSeconds) => _o.Time.GetUtcNow().AddSeconds(Math.Clamp(timeoutSeconds, 0, CallLimitSeconds));


    private async Task<StatusResult> StatusAsync(StatusInput input, CancellationToken ct)
    {
        var result = await ForwardAsync<StatusInput, StatusResult>(McpToolNames.Status, input, ct);
        if (input.Compact ?? (input.ThreadIds is not { Count: > 0 }))
        {
            result.Replies = [];
            return result;
        }
        var replies = new List<ReceivedMessage>();
        foreach (var reply in result.Replies.Where(r => !IsCodeForRunner(r))) replies.Add(await OpenAsync(reply, ct));
        result.Replies = replies;
        return result;
    }

    private async Task<WaitResult> WaitAsync(WaitInput input, CancellationToken ct)
    {
        var deadline = Deadline(input.TimeoutSeconds);
        WaitResult result;
        while (true)
        {
            result = await ForwardAsync<WaitInput, WaitResult>(McpToolNames.Wait, input with { TimeoutSeconds = Chunk(deadline) }, ct);
            if (!result.TimedOut || _o.Time.GetUtcNow() >= deadline) break;
        }
        var replies = new List<ReceivedMessage>();
        foreach (var reply in result.Replies.Where(r => !IsCodeForRunner(r))) replies.Add(await OpenAsync(reply, ct));
        result.Replies = replies;
        Seen(replies.Select(r => r.Envelope.Id));
        return result;
    }

    /// <summary>
    /// Asks a person to approve a step (the original issue). The approver reads the request in the dashboard, so it isn't
    /// end-to-end encrypted: it is checked for secrets here first, like a message body.
    /// </summary>
    private async Task<ApprovalResult> RequestApprovalAsync(RequestApprovalInput input, CancellationToken ct)
    {
        foreach (var (field, text) in new[] { ("title", input.Title), ("detail", input.Detail) })
        {
            if (text is { Length: > 0 } && SecretScanner.Scan(text) is { Count: > 0 } findings)
                throw new McpToolException(new McpError
                {
                    Code = McpErrorCodes.InvalidInput, Field = field,
                    Message = $"The {field} looks like it holds a secret ({string.Join(", ", findings.Select(f => f.Kind).Distinct())}). Take it out and ask again.",
                });
        }
        var projectId = input.ProjectId ?? DefaultProject();
        return await ForwardAsync<RequestApprovalInput, ApprovalResult>(McpToolNames.RequestApproval, input with { ProjectId = projectId }, ct);
    }

    /// <summary>Waits for the decision in chunks, returning by the client's limit like <c>wait</c>.</summary>
    private async Task<ApprovalResult> WaitApprovalAsync(WaitApprovalInput input, CancellationToken ct)
    {
        var deadline = Deadline(input.TimeoutSeconds);
        while (true)
        {
            var result = await ForwardAsync<WaitApprovalInput, ApprovalResult>(McpToolNames.WaitApproval, input with { TimeoutSeconds = Chunk(deadline) }, ct);
            if (result.Status != ApprovalState.Pending || _o.Time.GetUtcNow() >= deadline) return result;
        }
    }

    private async Task<WatchResult> WatchAsync(WatchInput input, CancellationToken ct)
    {
        var deadline = Deadline(input.TimeoutSeconds);
        while (true)
        {
            var result = await ForwardAsync<WatchInput, WatchResult>(McpToolNames.Watch, input with { TimeoutSeconds = Chunk(deadline) }, ct);
            if (result.Events.Count > 0 || _o.Time.GetUtcNow() >= deadline) return result;
            input = input with { After = result.Cursor ?? input.After };
        }
    }

    private int Chunk(DateTimeOffset deadline)
    {
        var left = deadline - _o.Time.GetUtcNow();
        return (int)Math.Ceiling(Math.Clamp(left.TotalSeconds, 0, _o.PollChunk.TotalSeconds));
    }

    private async Task<InboxResult> InboxAsync(InboxInput input, CancellationToken ct)
    {
        if (Delivery is { } task && input.InboxId is null)
        {
            // Started by the runner for this one task: it is the inbox.
            var envelope = task.Envelope;
            Seen([envelope.Id]);
            _tasks[envelope.ThreadId] = envelope;
            var received = await OpenAsync(new ReceivedMessage
            {
                Envelope = envelope,
                Delivery = new DeliveryInfo
                {
                    DeliveryId = task.DeliveryId, MessageId = envelope.Id, ThreadId = envelope.ThreadId, AgentId = task.AgentId,
                    MachineId = _o.MachineId, Role = task.Role, Attempt = task.Attempt, Status = DeliveryStatus.Leased, LeaseExpiresAt = task.LeaseExpiresAt,
                },
                Links = task.Links,
            }, ct);
            if (_o.SettleFile is { } settleFile) File.Delete(RoleKeyReadiness.FileName(settleFile));
            if (received.Links.Count == 0 && envelope.Attachments.Count > 0)
                received.Links = await _attachments.DeliveryLinksAsync(envelope.ThreadId, task.DeliveryId, ct);
            return new InboxResult { Messages = [received] };
        }

        var deadline = Deadline(input.TimeoutSeconds);
        InboxResult result;
        while (true)
        {
            result = await ForwardAsync<InboxInput, InboxResult>(McpToolNames.Inbox, new InboxInput
            {
                Role = input.Role, Max = input.Max, Peek = input.Peek, TimeoutSeconds = Chunk(deadline), InboxId = input.InboxId,
            }, ct);
            if (result.Messages.Count > 0 || _o.Time.GetUtcNow() >= deadline) break;
        }

        var messages = new List<ReceivedMessage>();
        foreach (var message in result.Messages)
        {
            var opened = await OpenAsync(message, ct);
            if (opened.Delivery is { } delivery)
            {
                _tasks[delivery.ThreadId] = opened.Envelope;
                if (opened.Envelope.Attachments.Count > 0)
                    opened.Links = await _attachments.DeliveryLinksAsync(delivery.ThreadId, delivery.DeliveryId, ct);
            }
            messages.Add(opened);
        }
        if (!input.Peek) Seen(messages.Select(m => m.Envelope.Id));
        return new InboxResult { Messages = messages };
    }

    // ---- this session's inbox (the original issue) ----

    /// <summary>The session inbox, while it is open.</summary>
    private SessionRecord? OpenSession => _session is { } s && s.ExpiresAt > _o.Time.GetUtcNow() ? s : null;

    /// <summary>A send from a session with an open inbox gets its replies there, unless it says where they go.</summary>
    private SendInput WithSessionReplyTo(SendInput input) =>
        input.ReplyTo is null && OpenSession is { } session ? input with { ReplyTo = session.ReplyTo } : input;

    /// <summary>Replies this session has read: the runner never resumes the session for them.</summary>
    private void Seen(IEnumerable<string> messageIds)
    {
        if (_session is { } session) _sessions.MarkSeen(session.InboxId, messageIds);
    }

    /// <summary>
    /// Opens this local server's inbox, or returns its explicitly owned/resumed inbox,
    /// and records on this machine which session it is, where it works, and how to
    /// tell it is still open, for the runner to resume it with a reply that comes after it closed.
    /// </summary>
    private async Task<OpenInboxResult> OpenInboxAsync(OpenInboxInput input, CancellationToken ct)
    {
        await _coordinationInboxGate.WaitAsync(ct);
        try { return await OpenInboxCoreAsync(input, ct); }
        finally { _coordinationInboxGate.Release(); }
    }

    private async Task<OpenInboxResult> OpenInboxCoreAsync(OpenInboxInput input, CancellationToken ct)
    {
        var now = _o.Time.GetUtcNow();
        if (now >= _nextSessionPrune)
        {
            _sessions.Prune(now, _session?.InboxId);
            _nextSessionPrune = now.AddHours(1);
        }
        var kind = _clientKind is "claude-code" or "codex" ? _clientKind : null;
        var folder = Path.GetFullPath(_o.WorkingDirectory);
        // Good for at least another hour, or a new one: replies to tasks sent now must still find it.
        bool Lasting(SessionRecord r) => r.ExpiresAt > (_coordinationAcquired ? now : now.AddHours(1));
        if (_coordinationAcquired && _session is { } coordinated && coordinated.ExpiresAt <= now)
            throw new McpToolException(new McpError { Code = McpErrorCodes.Conflict, Message = "This coordinator inbox expired. Stop dispatch and resolve the old project before starting a replacement." });

        var record = _session is { } mine && Lasting(mine) ? (_sessions.Find(mine.InboxId) ?? mine) : null;
        if (record is null)
        {
            var sessionId = AgentSessions.Newest(kind, folder, ClaudeHome, CodexHome, now);
            // Logs may identify the resumable conversation, never ownership of an inbox.
            // A fresh local server opens its own inbox unless the runner explicitly resumed one.
            var opened = await ForwardAsync<OpenInboxInput, OpenInboxResult>(McpToolNames.OpenInbox, input, ct);
            record = new SessionRecord
            {
                InboxId = opened.InboxId, WorkspaceId = _ws, AgentId = opened.AgentId, AgentKind = kind, Folder = folder,
                SessionId = sessionId, CreatedAt = now, ExpiresAt = opened.ExpiresAt,
            };
            _coordinationInboxId = opened.InboxId;
        }
        // Who holds the session open now: this mbus mcp, or (served by the runner, the original issue) the runner's run.
        record = _o.Scope is { } scope
            ? record with { RunDeliveryId = scope.DeliveryId, ProcessId = null, ProcessStartedAt = null }
            : record with { RunDeliveryId = null, ProcessId = Environment.ProcessId, ProcessStartedAt = ProcessStart() };
        // Keep the serving process separate from runner ownership for safe local maintenance.
        record = record with { HostProcessId = Environment.ProcessId, HostProcessStartedAt = ProcessStart() };
        _sessions.Save(record);
        _session = record;

        var resumable = record.SessionId is not null;
        return new OpenInboxResult
        {
            InboxId = record.InboxId, ReplyTo = record.ReplyTo, ExpiresAt = record.ExpiresAt, AgentId = record.AgentId,
            Resumable = resumable, SessionId = record.SessionId,
            Notice = resumable ? null : kind is null
                ? "Only Claude Code and Codex sessions can be resumed: a reply that arrives after this session closes starts a new session instead."
                : "No session log was found for this folder, so a reply that arrives after this session closes starts a new session instead.",
        };
    }

    private string ClaudeHome => _o.ClaudeHome ?? SessionUsage.ClaudeHome(Environment.GetEnvironmentVariable);

    private string CodexHome => _o.CodexHome ?? SessionUsage.CodexHome(Environment.GetEnvironmentVariable);

    private static DateTimeOffset? ProcessStart()
    {
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The message with its body decrypted (left empty, with a log line, when this machine can't open it). Once
    /// it is open, the ciphertext and key material are dropped: they only made results large (the original issue).
    /// </summary>
    private async Task<ReceivedMessage> OpenAsync(ReceivedMessage message, CancellationToken ct)
    {
        if (message.Envelope.Encryption is not { } encryption || message.Envelope.Body.Data is not { } data) return message;
        try
        {
            var body = await _keys.OpenAsync(Convert.FromBase64String(data), encryption, ct);
            var envelope = message.Envelope with
            {
                Body = message.Envelope.Body with { Data = null },
                Encryption = null,
                Attachments = [.. message.Envelope.Attachments.Select(a => a with { Encryption = null })],
            };
            return message with { Envelope = envelope, Body = body, Decrypted = true };
        }
        catch (CryptographicException e)
        {
            await _o.Log.WriteLineAsync($"mbus mcp: can't open message {message.Envelope.Id}: {e.Message}");
            return message;
        }
    }

    private async Task<ProgressResult> ProgressAsync(ProgressInput input, CancellationToken ct) =>
        await ForwardAsync<ProgressInput, ProgressResult>(McpToolNames.Progress,
            IsRunnerTask(input.ThreadId, input.DeliveryId) ? input with { DeliveryId = RunnerDeliveryId(Delivery!) } : input, ct);

    private async Task<SettleOutput> SettleAsync(SettleInput input, CancellationToken ct)
    {
        if (!IsRunnerTask(input.ThreadId, input.DeliveryId) || _o.SettleFile is null)
            return await SettleCoreAsync(input, runnerTask: false, ct);

        // A pinned settle must clear any fallback written by an earlier call.
        await _runnerSettleGate.WaitAsync(ct);
        try
        {
            return await SettleCoreAsync(input with { DeliveryId = RunnerDeliveryId(Delivery!) }, runnerTask: true, ct);
        }
        finally
        {
            _runnerSettleGate.Release();
        }
    }

    private async Task<SettleOutput> SettleCoreAsync(SettleInput input, bool runnerTask, CancellationToken ct)
    {
        var pinned = input.Attach?.Any(spec => spec is { Format: AttachmentFormat.GitBundle, ExpectedHead: not null }) == true;
        if (runnerTask && pinned)
            File.Delete(KeptAnswer.KeptFile(_o.SettleFile!));

        if (input.Friction is { Count: > 0 } notes)
        {
            if (notes.Count > 12) throw McpToolException.Invalid("friction", "Give at most 12 friction notes.");
            foreach (var (note, i) in notes.Select((n, i) => (n, i)))
            {
                if (note is null || !FrictionKinds.IsValid(note.Kind))
                    throw McpToolException.Invalid($"friction[{i}].kind", "Use code-mismatch, build-or-test, refused, missing-context, merge-or-rebase or other.");
                if (string.IsNullOrWhiteSpace(note.Note) || note.Note.Length > 500)
                    throw McpToolException.Invalid($"friction[{i}].note", "Give one sentence of 1 to 500 characters.");
            }
            if (input.SealedReply is not null) throw McpToolException.Invalid("friction", "Give plaintext reply with friction so the local tool can seal them together.");
            if (input.Reply is null && (input.Attach is { Count: > 0 } || input.Attachments is { Count: > 0 }))
                throw McpToolException.Invalid("reply", "Attachments go back with a reply: give reply as well.");
            var reply = input.Reply ?? new MessageBody { Summary = "Task friction reported." };
            input = input with
            {
                Reply = reply with { Friction = notes }, Friction = [], FrictionOnly = input.Reply is null,
                Result = input.Result with { FrictionKinds = notes.Select(n => n.Kind).ToList() },
            };
        }
        else if (input.Reply is { Friction.Count: > 0 })
            throw McpToolException.Invalid("friction", "Give notes in settle.friction so their kinds can be counted.");

        if (runnerTask)
        {
            input = input with { DeliveryId = RunnerDeliveryId(Delivery!) };
            if (_o.RecordVerifiedTaskSettle is not null)
            {
                if (input.Attach is { Count: > 0 } || input.Attachments is { Count: > 0 })
                    throw McpToolException.Invalid("attach", "For required verification, the runner returns the checked commit. Settle with your reply and leave the commit on the runner branch.");
                input = input with
                {
                    Result = input.Result with
                    {
                        Refs = (input.Result.Refs ?? []).Where(reference => reference.Kind != "git.commit" && reference.Kind != "git.branch").ToArray(),
                    },
                };
            }
        }

        if (input.Reply is not null || input.Attach is { Count: > 0 })
        {
            // Sealed for whoever reads the answer: the reply-to role's key, else the workspace key (the asker's machine holds it).
            var asked = await TaskEnvelopeAsync(input.ThreadId, ct);
            var key = await RecipientAsync(asked?.ReplyTo as RoleTarget, ct);
            if (input.Reply is { } plain) Scan(plain, "reply");
            if (input.Attach is { Count: > 0 } specs)
            {
                if (input.Reply is null && input.SealedReply is null) throw McpToolException.Invalid("reply", "Attachments go back with a reply: give reply as well.");
                // Where the reply goes, as the service will send it: the reply-to target, else the asking agent, else the task's target.
                MessageTarget to = asked?.ReplyTo
                                   ?? (asked?.Sender is { Kind: SenderKind.Agent, AgentId: { } agent } ? new AgentTarget { AgentId = agent } : null)
                                   ?? asked?.Target ?? throw McpToolException.NotFound($"Thread {input.ThreadId} wasn't found.");
                var attachments = new List<AttachmentRef>(input.Attachments ?? []);
                var refused = new List<RefusedAttachment>();
                foreach (var (spec, i) in specs.Select((s, i) => (s, i)))
                {
                    try
                    {
                        attachments.Add(await PackageAsync(await WithTaskBaseAsync(spec, asked, ct), to, key, input.ThreadId, $"attach[{i}]", ct));
                    }
                    catch (Exception e) when (runnerTask && !pinned && (e as McpToolException)?.Error.Field != $"attach[{i}].expectedHead"
                        && RefusedAttachment.Why(e) is { } why)
                    {
                        // the original issue: the agent can fix this; the others are still packaged, for the answer kept meanwhile.
                        refused.Add(new RefusedAttachment(i, spec, why.Code, why.Reason));
                    }
                }
                input = input with { Attach = [], Attachments = attachments };
                if (refused.Count > 0)
                {
                    // The call fails so the agent settles once, answer and code together; its answer is kept in case it never does.
                    var kept = RefusedAttachment.Kept(input, refused);
                    if (kept.Reply is { } keptReply) kept = kept with { Reply = null, SealedReply = Seal(keptReply, key, "reply") };
                    await File.WriteAllTextAsync(KeptAnswer.KeptFile(_o.SettleFile!), MessageBusJson.Serialize(kept), ct);
                    throw RefusedAttachment.Refusal(refused, _o.Scope?.Root ?? Path.GetDirectoryName(Path.GetFullPath(_o.SettleFile!)));
                }
            }
            if (input.Reply is { } reply)
            {
                input = input with { Reply = null, SealedReply = Seal(reply, key, "reply") };
            }
        }

        if (!runnerTask) return await ForwardAsync<SettleInput, SettleOutput>(McpToolNames.Settle, input, ct);
        return await SettleRunnerTaskAsync(input, ct);
    }

    /// <summary>
    /// A runner task's answer (sealed, its code packaged): the runner settles after the agent exits, with this result, so the
    /// answer is posted now and the result recorded for the runner. Any answer kept from a refused call is then dropped.
    /// </summary>
    private async Task<SettleOutput> SettleRunnerTaskAsync(SettleInput input, CancellationToken ct)
    {
        var output = input.SealedReply is null
            ? new SettleOutput { Status = ThreadStatus.Working }
            : await ForwardAsync<SettleInput, SettleOutput>(McpToolNames.Settle, input with { ReplyOnly = true }, ct);
        var recorded = input with { SealedReply = null, Attachments = [], ReplyOnly = false };
        if (_o.RecordVerifiedTaskSettle is { } record)
        {
            record(recorded);
        }
        else
        {
            await File.WriteAllTextAsync(_o.SettleFile!, MessageBusJson.Serialize(recorded), ct);
            File.Delete(KeptAnswer.KeptFile(_o.SettleFile!));
        }
        return output;
    }

    /// <summary>
    /// For the runner, once its agent has ended (the original issue): when the agent's settle never went through but an answer was kept
    /// from a refused call, sends that answer (with what did attach) and records its result as the agent's. True when sent.
    /// The kept file is in the run's folder, where the agent can write: it is held to the runner's scope (its own copy of the
    /// task's delivery and thread), never to the delivery file, and gets the scope's delivery.
    /// </summary>
    internal async Task<bool> SendKeptAnswerAsync(CancellationToken ct)
    {
        if (_o.SettleFile is not { } settleFile || _o.Scope is not { } scope) return false;
        var keptFile = KeptAnswer.KeptFile(settleFile);
        if (!File.Exists(keptFile)) return false;
        if (File.Exists(settleFile))
        {
            File.Delete(keptFile);
            return false;
        }
        SettleInput? kept;
        try
        {
            kept = MessageBusJson.Deserialize<SettleInput>(await File.ReadAllTextAsync(keptFile, ct));
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        if (kept is null || kept.ThreadId != scope.ThreadId || (kept.DeliveryId is not null && kept.DeliveryId != scope.DeliveryId)) return false;
        var element = JsonSerializer.SerializeToElement(kept, MessageBusJson.Options.GetTypeInfo(typeof(SettleInput)));
        var arguments = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        if (scope.Check(McpToolNames.Settle, arguments, new HashSet<string>(StringComparer.Ordinal)) is not null) return false;
        await SettleRunnerTaskAsync(kept with { DeliveryId = scope.DeliveryId }, ct);
        return true;
    }

    /// <summary>
    /// A reply's bundle or patch with no base starts after the code the task came with: that branch as this
    /// repository got it (<c>fetch_attachment</c>'s <c>refs/remotes/mbus/…</c>, or the runner's clone of the
    /// bundle, <c>refs/remotes/origin/…</c>), so only the new commits go back and the asker already has the rest.
    /// </summary>
    private async Task<AttachSpec> WithTaskBaseAsync(AttachSpec spec, MessageEnvelope? task, CancellationToken ct)
    {
        if (spec.Format == AttachmentFormat.File || spec.Base is not null) return spec;
        if (task?.Attachments.FirstOrDefault(a => a.Format == AttachmentFormat.GitBundle) is not { Branch: { Length: > 0 } branch }) return spec;
        var repository = LocalPath(spec.Path, "attach.path");
        foreach (var candidate in new[] { $"refs/remotes/mbus/{branch}", $"refs/remotes/origin/{branch}" })
        {
            try
            {
                return spec with { Base = await Git.CommitIdAsync(repository, candidate, ct) };
            }
            catch (GitException)
            {
            }
        }
        return spec;
    }

    /// <summary>A thread's messages as the service stores them (bodies still sealed), oldest first.</summary>
    private async Task<IReadOnlyList<MessageEnvelope>> ThreadMessagesAsync(string threadId, CancellationToken ct)
    {
        using var response = await _o.Http.GetAsync($"api/w/{Uri.EscapeDataString(_ws)}/threads/{Uri.EscapeDataString(threadId)}/messages", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        if (!response.IsSuccessStatusCode)
            throw McpToolException.Unavailable($"Couldn't read thread {threadId}'s messages ({(int)response.StatusCode}).");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Deserialize<MessageEnvelope>(MessageBusJson.Options)).OfType<MessageEnvelope>()];
    }

    /// <summary>The task message of a thread: from the runner's delivery or an earlier inbox call, else read from the service.</summary>
    private async Task<MessageEnvelope?> TaskEnvelopeAsync(string threadId, CancellationToken ct)
    {
        if (Delivery is { } d && d.Envelope.ThreadId == threadId) return d.Envelope;
        if (_tasks.TryGetValue(threadId, out var known)) return known;
        using var response = await _o.Http.GetAsync($"api/w/{Uri.EscapeDataString(_ws)}/threads/{Uri.EscapeDataString(threadId)}/messages", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
            ? doc.RootElement[0].Deserialize<MessageEnvelope>(MessageBusJson.Options)
            : null;
    }

    // ---- attachments ----

    private async Task<FetchAttachmentResult> FetchAttachmentAsync(FetchAttachmentInput input, CancellationToken ct)
    {
        var attachment = await FindAttachmentAsync(input.ThreadId, input.AttachmentId, ct)
                         ?? throw McpToolException.NotFound($"Attachment {input.AttachmentId} isn't on thread {input.ThreadId}.");
        var deliveryId = input.DeliveryId ?? (IsRunnerTask(input.ThreadId, null) ? RunnerDeliveryId(Delivery!) : null);
        DownloadLink link;
        if (deliveryId is not null)
        {
            var links = Delivery is { } d && d.DeliveryId == deliveryId && d.Links.Count > 0 && d.Links.All(l => l.ExpiresAt > _o.Time.GetUtcNow())
                ? d.Links
                : await _attachments.DeliveryLinksAsync(input.ThreadId, deliveryId, ct);
            link = links.FirstOrDefault(l => l.Id == attachment.Id)
                   ?? throw McpToolException.NotFound($"Delivery {deliveryId} has no link for attachment {attachment.Id}.");
        }
        else
        {
            link = await _attachments.DownloadLinkAsync(attachment.Id, ct);
        }

        if (attachment.Format == AttachmentFormat.GitBundle)
        {
            var repository = LocalPath(input.Repository, "repository");
            var into = input.Into ?? $"refs/remotes/mbus/{attachment.Branch}";
            string commit;
            try
            {
                commit = await _keys.WithRingAsync(ring => _attachments.ReceiveBundleAsync(attachment, link, ring, repository, into, ct), ct);
            }
            catch (GitException e) when (e.StandardError is { } stderr
                && Regex.IsMatch(stderr, @"(?m)^error: Repository lacks these prerequisite commits:\r?$"))
            {
                var commits = Regex.Matches(e.StandardError, @"(?m)^error: ([0-9a-f]{40}|[0-9a-f]{64})(?=\s|$)")
                    .Select(m => m.Groups[1].Value).Distinct();
                throw McpToolException.Invalid("repository", $"The bundle needs commits this repository doesn't have: {string.Join(", ", commits)}. Fetch into a repository that has them, or ask the sender to resend with base: \"\" for the whole history.");
            }
            _fetchedCode.Record(_ws, "attachment", attachment.Id, commit);
            return new FetchAttachmentResult { AttachmentId = attachment.Id, Format = attachment.Format, Commit = commit, Ref = into };
        }

        var path = input.Path is { Length: > 0 }
            ? LocalPath(input.Path, "path")
            : Path.Combine(TempFolders.Create(_o.TempDirectory, "mbus-attachment-").FullName, Path.GetFileName(attachment.Name));
        // A path in a folder that doesn't exist yet gets its folders (the original issue); LocalPath has already kept it inside the run folder.
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new McpToolException(new McpError { Code = McpErrorCodes.InvalidInput, Field = "path", Message = "Could not create the attachment folder: " + Reason(e) });
            }
            // Checked again now the folders exist, so one swapped for a link meanwhile can't lead the file outside the run.
            LocalPath(path, "path");
        }
        await _keys.WithRingAsync(async ring =>
        {
            await _attachments.DownloadToFileAsync(attachment, link, path, ring, ct);
            return true;
        }, ct);
        return new FetchAttachmentResult { AttachmentId = attachment.Id, Format = attachment.Format, Path = path };
    }

    private async Task<AttachmentRef?> FindAttachmentAsync(string threadId, string attachmentId, CancellationToken ct)
    {
        if (Delivery is { } d && d.Envelope.ThreadId == threadId && d.Envelope.Attachments.FirstOrDefault(a => a.Id == attachmentId) is { } fromTask)
            return fromTask;
        using var response = await _o.Http.GetAsync($"api/w/{Uri.EscapeDataString(_ws)}/threads/{Uri.EscapeDataString(threadId)}/messages", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        return doc.RootElement.EnumerateArray()
            .Select(e => e.Deserialize<MessageEnvelope>(MessageBusJson.Options))
            .SelectMany(e => e?.Attachments ?? [])
            .FirstOrDefault(a => a.Id == attachmentId);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAnsweringAsync();
        await ResetServiceAsync();
        // A tools connection can close while its run is still active. Keep runner ownership and
        // its host identity until the runner process exits; pruning conservatively retains these records.
        // Explicit coordinator recovery may have adopted another host's record, which we do not own.
        if (_session is { } session && _sessions.Find(session.InboxId) is { RunDeliveryId: null } saved
            && saved.HostProcessId == Environment.ProcessId
            && ProcessStart() is { } started && saved.HostProcessStartedAt == started)
        {
            try
            {
                // ProcessId protects an interactive session until its actual process exits, even if
                // this connection closes first. Only this connection host's maintenance metadata ends.
                _sessions.Save(saved with { HostProcessId = null, HostProcessStartedAt = null });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _keys.Dispose();
        _connect.Dispose();
        _coordinationInboxGate.Dispose();
        _runnerSettleGate.Dispose();
    }
}
