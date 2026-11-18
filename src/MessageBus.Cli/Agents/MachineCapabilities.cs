using MessageBus.Cli.Readiness;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Agents;

/// <summary>The base capability names the CLI can report automatically for a machine.</summary>
public static class MachineCapabilities
{
    /// <summary>All OS, agent and tool names mbus can report, regardless of this machine's capabilities; excludes version-specific tool capabilities.</summary>
    public static IEnumerable<string> Names =>
        Enum.GetValues<MachineOs>().Select(os => CapabilityNames.Format(CapabilityNames.OsPrefix, os == MachineOs.MacOs ? "macos" : os.ToString()))
            .Concat(Enum.GetValues<AgentKind>().Select(k => CapabilityNames.Format(CapabilityNames.AgentPrefix, AgentScanner.KindName(k))))
            .Concat(MachineTools.ToolNames.Select(t => CapabilityNames.Format(CapabilityNames.ToolPrefix, t)));
}
