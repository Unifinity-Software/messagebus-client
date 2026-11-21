using System.CommandLine;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Mcp;
using MessageBus.Cli.Server;

namespace MessageBus.Cli.Commands;

/// <summary>
/// <c>mbus mcp</c>: the local stdio MCP server agents start (their MCP settings, written by
/// <c>mbus connect</c>, run this). It loads the settings, builds the authenticated server client and
/// hands both to <see cref="CliHost.McpServer"/>. stdout belongs to MCP, so every message goes to stderr.
/// </summary>
internal static class McpCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var command = new Command("mcp", "Run the local MCP server that coding agents use (started by the agent, not by you).");
        command.SetAction(async (parse, ct) =>
        {
            // the original issue: started by an agent the runner started for one task. The runner serves the task's tools; this only
            // passes MCP through to it with the run's own credential, and never reads mbus's settings or the machine key.
            if (host.GetEnvironmentVariable(Runner.RunToolsVariables.File) is { Length: > 0 } runTools)
            {
                return await Runner.PipeRunTools.RelayAsync(runTools, host.OpenStandardInput(), host.OpenStandardOutput(), host.Error, ct);
            }

            if (MessageBus.Mcp.LocalServer.HasRunMarkers(host.GetEnvironmentVariable))
            {
                await host.Error.WriteLineAsync("mbus mcp: this run has no tools pipe; direct machine authentication is refused.");
                return 1;
            }

            var context = CommandContext.Load(host, parse, options);
            var settings = context.Settings;
            if (settings.Server is not { } server || !settings.CanAuthenticate(context.Store))
            {
                await host.Error.WriteLineAsync(ServerConnection.NotSignedIn().Message);
                return 1;
            }

            using var http = ServerConnection.CreateAuthenticatedClient(host, settings, context.Store, "mcp");
            var mcpContext = new McpHostContext
            {
                ServerUrl = server,
                Http = http,
                WorkspaceId = settings.Stored.WorkspaceId,
                MachineId = settings.Stored.MachineId,
                ConfigDirectory = context.Store.Directory,
                MachineKeyPem = context.Store.LoadKeyPem,
                Input = host.OpenStandardInput(),
                Output = host.OpenStandardOutput(),
                Log = host.Error,
                ClientVersion = CliHost.Version,
            };
            return await host.McpServer.RunAsync(mcpContext, ct);
        });
        return command;
    }
}
