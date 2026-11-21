using System.Text.Json;

namespace MessageBus.Contracts.Mcp;

/// <summary>Read-only discovery of execution recipes and workspace approvals for one repository.</summary>
public sealed record VerificationDirectory
{
    /// <summary>The version of the discovery registry.</summary>
    public required int RegistryVersion { get; init; }

    /// <summary>Whether the service can authorize and accept required verification requests.</summary>
    public required bool RequiredTaskRequestsSupported { get; init; }

    /// <summary>The execution recipes and checks known to the service.</summary>
    public required IReadOnlyList<VerificationRecipe> Recipes { get; init; }

    /// <summary>The workspace's approved environments for the requested repository.</summary>
    public required IReadOnlyList<VerificationEnvironmentApproval> Environments { get; init; }
}

/// <summary>A versioned execution recipe advertised for discovery.</summary>
public sealed record VerificationRecipe
{
    /// <summary>The stable recipe identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The recipe revision.</summary>
    public required int Revision { get; init; }

    /// <summary>The major Node.js version for a Node.js recipe.</summary>
    public int? NodeMajor { get; init; }

    /// <summary>The check keys supported by this recipe revision.</summary>
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>A workspace-approved environment advertised for discovery.</summary>
public sealed record VerificationEnvironmentApproval
{
    /// <summary>The identifier to use in a required verification request.</summary>
    public required string Id { get; init; }

    /// <summary>The service's opaque environment version, for display and diagnostics.</summary>
    public required JsonElement Version { get; init; }

    /// <summary>The environment's recipe identifier.</summary>
    public required string RecipeId { get; init; }

    /// <summary>The environment's recipe revision.</summary>
    public required int RecipeRevision { get; init; }

    /// <summary>The count of approved shared machines, independent of current availability.</summary>
    public required int ApprovedSharedMachineCount { get; init; }
}
