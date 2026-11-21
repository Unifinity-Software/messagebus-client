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

    /// <summary>Optional feature protocols; absence keeps existing operations compatible.</summary>
    public ServerCapabilities? Capabilities { get; init; }

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
}

/// <summary>Optional server protocols, independent of the product version.</summary>
public sealed record ServerCapabilities
{
    /// <summary>Role proposal protocol, present only when the complete implementation is enabled.</summary>
    public RoleProposalsCapability? RoleProposals { get; init; }
}
