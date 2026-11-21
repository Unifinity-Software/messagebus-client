using System.Text.Json.Serialization;

namespace MessageBus.Cli.Server;

/// <summary>An immutable saved credential/checkpoint fence, scoped to an exact API endpoint.</summary>
public sealed record ServerBindingSnapshot
{
    /// <summary>Reconstitutes a supported saved snapshot; malformed or future security state refuses.</summary>
    [JsonConstructor]
    public ServerBindingSnapshot(int version, string selectedEndpoint, string stampId, int authorityVersion,
        string issuer, string audience, int minimumMachineSignatureVersion, bool machineSignatureRequired)
    {
        if (version != 1 || authorityVersion != 1 || minimumMachineSignatureVersion != 2 || !machineSignatureRequired)
            throw new ServerBindingException("Unsupported saved server binding.");
        Endpoint = ServerEndpoint.Parse(selectedEndpoint);
        Binding = ServerBinding.Create(stampId, issuer, audience);
    }

    /// <summary>Saved snapshot schema.</summary>
    public int Version => 1;
    /// <summary>Exact normalized selected endpoint, including API base path.</summary>
    public string SelectedEndpoint => Endpoint.Value;
    /// <summary>Exact stamp ID.</summary>
    public string StampId => Binding.StampId;
    /// <summary>Supported authority version.</summary>
    public int AuthorityVersion => Binding.AuthorityVersion;
    /// <summary>Canonical credential authority.</summary>
    public string Issuer => Binding.Issuer;
    /// <summary>Canonical audience.</summary>
    public string Audience => Binding.Audience;
    /// <summary>Monotonic machine signature floor.</summary>
    public int MinimumMachineSignatureVersion => Binding.MinimumMachineSignatureVersion;
    /// <summary>Machine signing is mandatory.</summary>
    public bool MachineSignatureRequired => Binding.MachineSignatureRequired;
    /// <summary>Parsed exact scope.</summary>
    [JsonIgnore] public ServerEndpoint Endpoint { get; }
    /// <summary>Validated isolated tuple.</summary>
    [JsonIgnore] public ServerBinding Binding { get; }

    internal static ServerBindingSnapshot Create(ServerEndpoint endpoint, ServerBinding binding) =>
        new(1, endpoint.Value, binding.StampId, 1, binding.Issuer, binding.Audience, 2, true);
}
