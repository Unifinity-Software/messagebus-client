using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Server;

/// <summary>A send blocked during server binding. Integration must not retry credentials with v1 or another host.</summary>
public sealed class ServerBindingException(string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    internal bool IsTransientDiscoveryFailure { get; init; }
}

/// <summary>The immutable supported isolated binding tuple. This value does not itself authorize a send.</summary>
public sealed record ServerBinding
{
    private ServerBinding(string stampId, string issuer) { StampId = stampId; Issuer = issuer; }
    /// <summary>Exact process stamp ID.</summary>
    public string StampId { get; }
    /// <summary>Normalized canonical credential authority.</summary>
    public string Issuer { get; }
    /// <summary>The audience equals the canonical issuer in this protocol.</summary>
    public string Audience => Issuer;
    /// <summary>Supported authority descriptor version.</summary>
    public int AuthorityVersion => 1;
    /// <summary>Monotonic minimum machine signature version.</summary>
    public int MinimumMachineSignatureVersion => 2;
    /// <summary>Isolated machine signing is mandatory.</summary>
    public bool MachineSignatureRequired => true;

    /// <summary>Validates a tuple without inferring its authority from transport or metadata aliases.</summary>
    public static ServerBinding Create(string stampId, string issuer, string audience, bool allowLoopbackHttp = false)
    {
        try
        {
            if (stampId is not ("ca" or "eu" or "us")) throw new ArgumentException("Unsupported stamp ID.");
            var canonical = ProtocolOrigin.NormalizeIsolated(issuer, allowLoopbackHttp);
            if (ProtocolOrigin.NormalizeIsolated(audience, allowLoopbackHttp) != canonical)
                throw new ArgumentException("Issuer and audience differ.");
            return new ServerBinding(stampId, canonical);
        }
        catch (ArgumentException ex) { throw new ServerBindingException("Invalid isolated binding tuple.", ex); }
    }
}

/// <summary>An exact locally provisioned or previously persisted transport-to-authority binding.</summary>
public sealed record ServerBindingExpectation
{
    /// <summary>The optional trusted local provisioning variable; this core does not read process environment.</summary>
    public const string EnvironmentVariable = "MBUS_SERVER_BINDING";
    private ServerBindingExpectation(string origin, ServerBinding binding) { TransportOrigin = origin; Binding = binding; }
    /// <summary>The normalized transport origin; this is not the canonical signing authority for an alias.</summary>
    public string TransportOrigin { get; }
    /// <summary>The trusted expected tuple.</summary>
    public ServerBinding Binding { get; }
    /// <summary>Creates validated local evidence. Discovery aliases must never be passed as trusted evidence.</summary>
    public static ServerBindingExpectation Create(string transportOrigin, ServerBinding binding, bool allowLoopbackHttp = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        try { return new(ProtocolOrigin.NormalizeIsolated(transportOrigin, allowLoopbackHttp), binding); }
        catch (ArgumentException ex) { throw new ServerBindingException("Invalid expected transport origin.", ex); }
    }

    /// <summary>Parses the exact trusted local MBUS_SERVER_BINDING JSON input; absence must be handled explicitly by the caller.</summary>
    public static ServerBindingExpectation Parse(string json, bool allowLoopbackHttp = false) =>
        ServerDiscoveryParser.ParseExpectation(json, allowLoopbackHttp);
}

/// <summary>Evidence collected from the ledger and every surviving credential/checkpoint snapshot under one lock.</summary>
public sealed class ServerBindingFloor
{
    private ServerBindingFloor(bool readable, IReadOnlyList<ServerBindingExpectation> bindings) { Readable = readable; Bindings = bindings; }
    internal bool Readable { get; }
    internal IReadOnlyList<ServerBindingExpectation> Bindings { get; }
    /// <summary>Unknown, corrupt, contradictory or incompletely read binding-bearing state always refuses.</summary>
    public static ServerBindingFloor Unknown { get; } = new(false, []);
    /// <summary>Requires an explicit complete state read. An empty collection asserts no ever-pin survives anywhere.</summary>
    public static ServerBindingFloor FromCompleteState(IEnumerable<ServerBindingExpectation> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var copy = bindings.ToArray();
        if (copy.Any(b => b is null) || copy.GroupBy(b => b.TransportOrigin, StringComparer.Ordinal)
            .Any(group => group.Select(b => b.Binding).Distinct().Count() != 1)) return Unknown;
        return new(true, copy);
    }
}

/// <summary>A pure policy result. It cannot authorize credential transmission or replace current discovery and durable writes.</summary>
public sealed record ServerBindingDecision
{
    internal ServerBindingDecision(ServerBinding? binding) => Binding = binding;
    /// <summary>Null only for affirmative never-pinned legacy discovery.</summary>
    public ServerBinding? Binding { get; }
    /// <summary>True when integration must durably reconcile ledger and snapshots before sending credentials.</summary>
    public bool RequiresIsolatedPersistence => Binding is not null;
    /// <summary>The protocol to use after all external persistence and current-send gates succeed.</summary>
    public int MachineSignatureVersion => Binding is null ? 1 : 2;
}

/// <summary>Pure monotonic policy; no HTTP, persistence, credential migration or caching is performed.</summary>
public static class ServerBindingPolicy
{
    /// <summary>Evaluates one current anonymous response and complete local evidence for an exact selected endpoint.</summary>
    public static ServerBindingDecision Evaluate(ServerEndpoint selected, ServerDiscovery discovery,
        ServerBindingFloor floor, ServerBindingExpectation? expected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(floor);
        if (!floor.Readable) throw new ServerBindingException("Binding state is unknown or corrupt.");
        if (expected is not null && expected.TransportOrigin != selected.Origin)
            throw new ServerBindingException("Expected binding is for a different selected origin.");
        var evidence = floor.Bindings.Where(b => b.TransportOrigin == selected.Origin).ToList();
        if (expected is not null) evidence.Add(expected);
        var pin = evidence.FirstOrDefault()?.Binding;
        if (evidence.Any(e => e.Binding != pin)) throw new ServerBindingException("Conflicting surviving isolated bindings.");
        if (discovery.Binding is null)
        {
            if (pin is not null) throw new ServerBindingException("Isolated discovery disappeared; downgrade refused.");
            return new ServerBindingDecision(null);
        }
        if (pin is not null && pin != discovery.Binding) throw new ServerBindingException("Isolated authority changed.");
        if (selected.Origin != discovery.Binding.Issuer
            && (pin is null || !discovery.Aliases.Contains(selected.Origin, StringComparer.Ordinal)))
            throw new ServerBindingException("Selected alias lacks exact local and server authorization.");
        return new ServerBindingDecision(discovery.Binding);
    }
}
