using System.Text.Json;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Output;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Commands;

/// <summary>
/// What the server says about this machine, for <c>mbus doctor</c> (the original issue): its record (online or not, the version it
/// last reported, tasks at once, when its runner was last connected), its agents' roles (the original issue) and the workspace's
/// release channel. Anonymous identity discovery, then three authenticated reads at once when signed in, a few seconds at most. Not when it
/// was last seen: the server counts every signed request, this one too; when its runner was last connected is kept apart.
/// The workspace's current name comes with the release channel (the original issue) and is kept for the header.
/// </summary>
internal sealed record DoctorServerView(MachineInfo? Machine, RunnerReleasePolicy? Releases, string? Problem, string? WorkspaceName = null, bool Suspended = false)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>This machine's agents as the server has them, with their roles; null when the server didn't say.</summary>
    public IReadOnlyList<AgentInfo>? Agents { get; init; }

    /// <summary>When the server was asked, to say how long ago the runner was connected.</summary>
    public DateTimeOffset AskedAt { get; init; }

    // A diagnostic observation never authorizes a later request. Each authenticated read still
    // prepares and consumes its own fresh send through ServerConnection's shared gate.
    internal ServerDiscovery? Discovery { get; init; }
    internal ServerBindingSnapshot? PreparedBinding { get; init; }
    internal ServerBinding? PersistedBinding { get; init; }

    /// <summary>A security refusal blocks readiness; an optional server-view lookup failure only warns.</summary>
    internal bool BindingRefused { get; init; }

    /// <summary>Asks the server, when this machine is signed in to one.</summary>
    public static async Task<DoctorServerView> AskAsync(CommandContext context, CancellationToken ct)
    {
        var settings = context.Settings;
        if (settings.Server is null)
        {
            return new(null, null, null);
        }
        using var timeout = new CancellationTokenSource(Timeout, context.Host.Time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        PreparedServerSend? observation = null;
        ServerBinding? persistedBinding = null;
        try
        {
            var endpoint = ServerEndpoint.Parse(settings.Server.AbsoluteUri);
            var floor = await context.Store.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.LoadBindingFloor()), cts.Token).ConfigureAwait(false);
            if (!floor.Readable) throw new ServerBindingException("The saved binding floor is unreadable.");
            persistedBinding = floor.Bindings.FirstOrDefault(pin => pin.TransportOrigin == endpoint.Origin)?.Binding;
            using var gate = new ServerSendGate(context.Host, context.Store, settings.Server);
            observation = await gate.PrepareSendAsync(new Uri(CliSettings.BaseAddress(settings.Server), "api/server"), cts.Token).ConfigureAwait(false);
            if (!settings.CanAuthenticate(context.Store)
                || settings.Stored.MachineId is not { } machineId || settings.Stored.WorkspaceId is not { } workspace)
                return new(null, null, null) { Discovery = observation.Discovery, PreparedBinding = observation.Binding, PersistedBinding = persistedBinding };
            using var http = ServerConnection.CreateAuthenticatedClient(context.Host, settings, context.Store, "cli");
            var ws = Uri.EscapeDataString(workspace);
            var id = Uri.EscapeDataString(machineId);
            var machine = GetAsync(http, $"{ApiRoutes.Machines.Replace("{workspaceId}", ws, StringComparison.Ordinal)}/{id}", cts.Token);
            var policy = PolicyAsync(http, cts.Token);
            var agents = AgentsAsync(http, cts.Token);
            await Task.WhenAll(machine, policy, agents).ConfigureAwait(false);
            var info = await machine.ConfigureAwait(false) is { } m ? MessageBusJson.Deserialize<MachineInfo>(m) : null;
            var work = await policy.ConfigureAwait(false);
            context.Store.RememberWorkspaceName(workspace, work?.WorkspaceName);
            return new(info, work?.Releases, info is null ? "the server doesn't know this machine (removed from the workspace?). Fix: mbus login." : null,
                work?.WorkspaceName)
            {
                Agents = await agents.ConfigureAwait(false),
                AskedAt = context.Host.Time.GetUtcNow(),
                Discovery = observation.Discovery,
                PreparedBinding = observation.Binding,
                PersistedBinding = persistedBinding,
            };
        }
        catch (ServerBindingException)
        {
            ct.ThrowIfCancellationRequested();
            return new(null, null, "Server binding validation refused. Restore the saved binding or enroll explicitly at the selected endpoint; credentials were not retried.")
            { Discovery = observation?.Discovery, PreparedBinding = observation?.Binding, PersistedBinding = persistedBinding, BindingRefused = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            ct.ThrowIfCancellationRequested();
            var problem = ex is OperationCanceledException ? $"no answer within {Timeout.TotalSeconds:0} seconds." : ex.Message;
            // Until discovery and durable preparation finish, readiness is unknown. A timeout
            // here is a binding refusal too; only subsequent optional lookups may warn.
            return new(null, null, observation is null ? "Server binding validation refused: " + problem : problem, Suspended: ex is WorkspaceSuspendedException)
            { Discovery = observation?.Discovery, PreparedBinding = observation?.Binding, PersistedBinding = persistedBinding, BindingRefused = observation is null };
        }
    }

    /// <summary>This machine's work policy, as the runner reads it (release settings, workspace name); null when the server doesn't say.</summary>
    private static async Task<RunnerWorkPolicy?> PolicyAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            return await new HttpRunnerServiceClient(http).PolicyAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // the channel is a nicety; the machine's record is what matters
        }
    }

    /// <summary>This machine's agents and their roles (the original issue); null when the server doesn't say (one older than the original issue answers 404).</summary>
    private static async Task<IReadOnlyList<AgentInfo>?> AgentsAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            return await GetAsync(http, ApiRoutes.MachineSelfAgents, ct).ConfigureAwait(false) is { } body
                ? MessageBusJson.Deserialize<IReadOnlyList<AgentInfo>>(body)
                : null;
        }
        catch (HttpRequestException)
        {
            return null; // the roles are a nicety; the machine's record is what matters
        }
    }

    /// <summary>The body of a successful GET; null for 404; throws for any other failure.</summary>
    private static async Task<string?> GetAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(ServerConnection.Relative(path), ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            if (await SuspensionRefusal.ReadAsync(response, ct) is { } suspension)
                throw new WorkspaceSuspendedException(suspension);
            throw new HttpRequestException($"the server answered {(int)response.StatusCode} to GET {path.Split('?')[0]}.", null, response.StatusCode);
        }
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The summary's rows: online or not, the version the server has, when its runner was last connected, the agents' roles.</summary>
    public IEnumerable<SummaryRow> Rows()
    {
        if (Problem is { } problem)
        {
            yield return new("Server's view", problem, BindingRefused || Suspended ? SummaryTone.Bad : SummaryTone.Note, Suspended ? "suspended:" : "couldn't ask:");
            yield break;
        }
        if (Machine is not { } machine)
        {
            yield break;
        }
        var version = machine.CliVersion is { Length: > 0 } v ? $"has mbus {v}" : "has no version for it";
        yield return machine.PendingApproval
            ? new("Server's view", $"{version}. Its owner approves it on the Runners page.", SummaryTone.Note, "waiting for approval:")
            : machine.Online
                ? new("Server's view", $"{version}", SummaryTone.Good, "online:")
                : new("Server's view", $"{LastConnected(machine.RunnerSeenAt)}; {version}. Start one with mbus runner.", SummaryTone.Note, "offline:");
        if (machine.SlotCeiling is { } ceiling)
        {
            yield return new("Tasks at once", machine.SlotSetting is { } set
                ? $"{set}, set on the dashboard (up to {ceiling})"
                : $"{machine.Slots}; the dashboard can set up to {ceiling}");
        }
        if (Agents is { } agents)
        {
            yield return Roles(agents);
        }
    }

    /// <summary>When a runner was last connected, as the server kept it (the original issue).</summary>
    private string LastConnected(DateTimeOffset? at) =>
        at is { } seen ? $"its runner was last connected {Ago(AskedAt - seen)}" : "no runner has connected yet";

    /// <summary>Each of this machine's agents with the roles it holds, as the server has them (the original issue).</summary>
    private static SummaryRow Roles(IReadOnlyList<AgentInfo> agents)
    {
        if (agents.Count == 0)
        {
            return new("Roles", "the server has no agents for this machine. Fix: mbus connect --rescan.", SummaryTone.Note, "none:");
        }
        var lines = agents.OrderBy(a => AgentScanner.DisplayName(a.Kind), StringComparer.Ordinal)
            .Select(a => $"{AgentScanner.DisplayName(a.Kind)}: {(a.Roles.Count == 0 ? "no roles yet" : string.Join(", ", a.Roles.Order(StringComparer.Ordinal)))}");
        var text = string.Join('\n', lines);
        return agents.All(a => a.Roles.Count == 0)
            ? new("Roles", text + "\nTasks sent to a role only reach agents that hold it: add one on the Runners page.", SummaryTone.Note)
            : new("Roles", text);
    }

    private static string Ago(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)age.TotalMinutes} min ago",
        < 48 * 60 => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} days ago",
    };
}
