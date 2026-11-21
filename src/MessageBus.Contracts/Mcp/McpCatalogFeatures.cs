namespace MessageBus.Contracts.Mcp;

/// <summary>Optional catalog features explicitly supported by the service's installed handlers.</summary>
[Flags]
public enum McpCatalogFeatures
{
    /// <summary>No optional service features.</summary>
    None = 0,
    /// <summary>Required task verification sends and their request schemas.</summary>
    VerifiedTasks = 1,
    /// <summary>Reserved for task retargeting integration.</summary>
    Retarget = 2,
    /// <summary>Reserved for role proposal integration.</summary>
    RoleProposals = 4,
}
