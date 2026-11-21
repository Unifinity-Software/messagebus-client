namespace MessageBus.Contracts.Mcp;

/// <summary>Optional capabilities a deployed handler supports when building its MCP tool catalog.</summary>
[Flags]
public enum McpCatalogFeatures
{
    /// <summary>Expose only the established tool contracts.</summary>
    None = 0,

    /// <summary>The handler supports verified task workflows.</summary>
    VerifiedTasks = 1,

    /// <summary>The handler supports retargeting queued tasks.</summary>
    Retarget = 2,

    /// <summary>The handler supports role proposals.</summary>
    RoleProposals = 4,
}
