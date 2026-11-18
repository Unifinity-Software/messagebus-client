using System.Reflection;

namespace MessageBus.Cli.Mcp;

/// <summary>How an agent starts the local MCP server: <see cref="Command"/> with <see cref="Args"/>.</summary>
/// <param name="Command">Full path of the executable.</param>
/// <param name="Args">Arguments, ending with <c>mcp</c>.</param>
public sealed record McpLaunchCommand(string Command, IReadOnlyList<string> Args)
{
    /// <summary>
    /// What starts this mbus on a cmd.exe line (the Windows runner service), when that isn't <see cref="Command"/> plus
    /// the arguments before <c>mcp</c>: a dotnet tool install's <c>mbus.cmd</c> (the original issue).
    /// </summary>
    public IReadOnlyList<string>? CommandLineStart { get; init; }

    /// <summary>
    /// This mbus: its own executable (the published single file, or the apphost in bin when run from
    /// source), or <c>dotnet mbus.dll</c> when started through the dotnet host.
    /// </summary>
    public static McpLaunchCommand ForCurrentProcess()
    {
        // Framework-dependent tools run through dotnet; locate the package beside its managed entry point.
        var tool = Update.DotnetToolInstall.Running;
        if (tool?.Shim is { } toolShim)
        {
            return toolShim.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? new McpLaunchCommand(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", toolShim, "mcp"]) { CommandLineStart = [toolShim] }
                : new McpLaunchCommand(toolShim, ["mcp"]) { CommandLineStart = [toolShim] };
        }
        // the original issue: an updated mbus runs from its version's folder, which a later update deletes; the mbus on PATH stays.
        var processPath = Environment.ProcessPath is { } running ? Update.VersionedInstall.StableFor(running) ?? running : "mbus";
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Only possible when not published as a single file, so mbus.dll is beside us.
            var dll = Path.Combine(AppContext.BaseDirectory, (Assembly.GetEntryAssembly()?.GetName().Name ?? "mbus") + ".dll");
            return new McpLaunchCommand(processPath, [dll, "mcp"]);
        }
        return new McpLaunchCommand(processPath, ["mcp"]);
    }
}
