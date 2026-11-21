using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Mcp;

/// <summary>Optional capabilities a deployed handler supports when building its MCP tool catalog.</summary>
[Flags]
public enum McpCatalogFeatures
{
    /// <summary>Expose only the established tool contracts.</summary>
    [JsonStringEnumMemberName("none")] None = 0,

    /// <summary>The handler supports verified task workflows.</summary>
    [JsonStringEnumMemberName("verified-tasks")] VerifiedTasks = 1,

    /// <summary>The handler supports retargeting queued tasks.</summary>
    [JsonStringEnumMemberName("retarget")] Retarget = 2,

    /// <summary>The handler supports role proposals.</summary>
    [JsonStringEnumMemberName("role-proposals")] RoleProposals = 4,
}
