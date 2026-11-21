using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Mcp;

/// <summary>Optional catalog features explicitly supported by the service's installed handlers.</summary>
[Flags]
public enum McpCatalogFeatures
{
    /// <summary>No optional service features.</summary>
    [JsonStringEnumMemberName("none")] None = 0,
    /// <summary>Required task verification sends and their request schemas.</summary>
    [JsonStringEnumMemberName("verified-tasks")] VerifiedTasks = 1,
    /// <summary>Reserved for task retargeting integration.</summary>
    [JsonStringEnumMemberName("retarget")] Retarget = 2,
    /// <summary>Reserved for role proposal integration.</summary>
    [JsonStringEnumMemberName("role-proposals")] RoleProposals = 4,
}
