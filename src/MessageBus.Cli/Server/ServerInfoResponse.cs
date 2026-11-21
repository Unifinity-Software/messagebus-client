using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Server;

/// <summary><c>GET /api/server</c> (answered by the hosted service): what a server URL points at.</summary>
public sealed record ServerInfoResponse
{
    /// <summary>Always <c>messagebus.ai</c> for a messagebus.ai server.</summary>
    public string? Product { get; init; }

    /// <summary>The server's version.</summary>
    public string? Version { get; init; }

    /// <summary>The address other machines use.</summary>
    public string? Url { get; init; }

    /// <summary>How people sign in.</summary>
    public ServerSignIn? SignIn { get; init; }

    /// <summary>Descriptive stamp information; this alone never selects isolated authentication.</summary>
    public ServerStamp? Stamp { get; init; }

    /// <summary>Optional feature protocol tokens; absence keeps existing operations compatible.</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Optional role proposal protocol descriptor, advertised alongside its capability token.</summary>
    public RoleProposalsCapability? RoleProposals { get; init; }

    /// <summary>The product name every messagebus.ai server reports.</summary>
    public const string ProductName = "messagebus.ai";
}

/// <summary>Sign-in settings from <c>/api/server</c>.</summary>
public sealed record ServerSignIn
{
    /// <summary>Whether Microsoft sign-in is set up on the server.</summary>
    public bool Microsoft { get; init; }

    /// <summary>
    /// The dashboard page where the person approves <c>mbus login</c> (the original issue); mbus adds <c>?code=…</c>.
    /// </summary>
    public string? CliApprovalUrl { get; init; }

    /// <summary>Optional isolated authority descriptor; use the strict parser before making security decisions.</summary>
    public ServerAuthority? Authority { get; init; }

    /// <summary>Optional isolated signature requirement; explicit null is not legacy evidence.</summary>
    public ServerMachineSignature? MachineSignature { get; init; }
}

/// <summary>The existing descriptive stamp envelope.</summary>
public sealed record ServerStamp
{
    /// <summary>Process stamp ID.</summary>
    public string? StampId { get; init; }
    /// <summary>Descriptive geography, not proof of availability or residency.</summary>
    public string? Geography { get; init; }
    /// <summary>Descriptive Azure region.</summary>
    public string? AzureRegion { get; init; }
    /// <summary>Canonical origin in isolated mode.</summary>
    public string? Url { get; init; }
    /// <summary>Configured server aliases; these cannot authorize client credential migration.</summary>
    public string[]? Aliases { get; init; }
}

/// <summary>Optional authority descriptor version 1.</summary>
public sealed record ServerAuthority
{
    /// <summary>Descriptor version.</summary>
    public int? Version { get; init; }
    /// <summary>Process stamp ID.</summary>
    public string? StampId { get; init; }
    /// <summary>Canonical issuer origin.</summary>
    public string? Issuer { get; init; }
    /// <summary>Canonical audience origin.</summary>
    public string? Audience { get; init; }
}

/// <summary>Optional mandatory machine signature descriptor.</summary>
public sealed record ServerMachineSignature
{
    /// <summary>Signature version.</summary>
    public int? Version { get; init; }
    /// <summary>Must be true for isolated authentication.</summary>
    public bool? Required { get; init; }
}

/// <summary>Optional server protocols, independent of the product version.</summary>
public sealed record ServerCapabilities
{
    /// <summary>Role proposal protocol, present only when the complete implementation is enabled.</summary>
    public RoleProposalsCapability? RoleProposals { get; init; }
}
