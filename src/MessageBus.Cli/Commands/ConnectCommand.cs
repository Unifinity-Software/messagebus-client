using System.CommandLine;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Connect;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Readiness;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary>
/// <c>mbus connect [agent]</c>: sets up each installed agent to use messagebus.ai (its MCP settings
/// start <c>mbus mcp</c>, and the orchestrate skill is installed), after showing every file it will
/// change. <c>--rescan</c> only repeats the agent scan and reports it to the server.
/// </summary>
internal static class ConnectCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var agent = new Argument<string?>("agent")
        {
            Description = "Only set up this agent: claude, codex, copilot or opencode.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var rescan = new Option<bool>("--rescan") { Description = "Look for agents again and update the dashboard. Changes no files." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Make the changes without asking." };
        var json = new Option<bool>("--json") { Description = "With --rescan: print the scan as JSON." };

        var command = new Command("connect", "Set up this machine's coding agents to use messagebus.ai.") { agent, rescan, yes, json };
        command.SetAction((parse, ct) =>
        {
            var context = CommandContext.Load(host, parse, options);
            return parse.GetValue(rescan)
                ? RescanAsync(context, parse.GetValue(json), ct)
                : ConnectAsync(context, parse.GetValue(agent), parse.GetValue(yes), ct);
        });
        return command;
    }

    private static async Task<int> RescanAsync(CommandContext context, bool json, CancellationToken ct)
    {
        var keyCheck = await WorkspaceKeyReadiness.CheckAsync(context, ct);
        if (keyCheck.CouldNotCheck) await context.Host.Error.WriteLineAsync(keyCheck.Problem);
        var inventory = await AgentScanner.BuildInventoryAsync(context.Host, context.Settings.Stored.MachineId, context.Settings.Location, ct);
        inventory = WorkspaceKeyReadiness.Apply(inventory, keyCheck);
        var sent = await TrySendInventoryAsync(context, inventory, ct);
        if (json)
        {
            await context.Host.Out.WriteLineAsync(MessageBusJson.Serialize(inventory));
        }
        else
        {
            context.WriteAgents(inventory.Agents);
            context.Console.MarkupLine(sent ? "The dashboard has the new list." : "[grey]This machine isn't signed in, so the dashboard wasn't updated. Run mbus login first.[/]");
        }
        return 0;
    }

    private static async Task<int> ConnectAsync(CommandContext context, string? agentName, bool yes, CancellationToken ct)
    {
        var host = context.Host;
        var console = context.Console;
        var scan = await AgentScanner.ScanAsync(host, ct);

        List<DetectedAgent> targets;
        if (agentName is not null)
        {
            var kind = ParseAgent(agentName);
            var detected = scan.First(a => a.Kind == kind);
            if (!detected.Found)
            {
                throw new CliException($"{AgentScanner.DisplayName(kind)} isn't installed here (no '{AgentScanner.CommandFor(kind)}' command was found).");
            }
            targets = [detected];
        }
        else
        {
            targets = [.. scan.Where(a => a.Found && AgentConnector.Supported.Contains(a.Kind))];
            foreach (var other in scan.Where(a => a.Found && !AgentConnector.Supported.Contains(a.Kind)))
            {
                console.MarkupLineInterpolated($"[grey]{AgentScanner.DisplayName(other.Kind)} is installed, but mbus connect can't set it up yet.[/]");
            }
            if (targets.Count == 0)
            {
                throw new CliException("No agent mbus can set up was found. Install Claude Code, Codex, GitHub Copilot CLI or OpenCode, then run mbus connect again.");
            }
        }

        var changes = targets.SelectMany(t => AgentConnector.Plan(host, t.Kind)).ToList();
        console.MarkupLine("mbus connect will change these files:");
        foreach (var change in changes)
        {
            console.MarkupLineInterpolated($"  [bold]{AgentScanner.DisplayName(change.Agent)}[/]: {change.What}");
            console.MarkupLineInterpolated($"    {change.Path}");
        }
        if (host.Skills.GetFiles().Count == 0)
        {
            console.MarkupLine("[grey]The orchestrate skill isn't in this build of mbus yet, so only the MCP server is set up. Run mbus connect again after updating mbus.[/]");
        }

        if (!yes && !context.Confirm("Make these changes?"))
        {
            console.MarkupLine("Nothing was changed.");
            return 1;
        }

        foreach (var change in changes)
        {
            change.Apply();
        }
        console.MarkupLine("[green]Done.[/] Start a new session in each agent to pick up the mbus tools.");

        // The dashboard shows which agents use mbus; tell it.
        var keyCheck = await WorkspaceKeyReadiness.CheckAsync(context, ct);
        if (keyCheck.CouldNotCheck) await context.Host.Error.WriteLineAsync(keyCheck.Problem);
        var inventory = await AgentScanner.BuildInventoryAsync(host, context.Settings.Stored.MachineId, context.Settings.Location, ct);
        inventory = WorkspaceKeyReadiness.Apply(inventory, keyCheck);
        await TrySendInventoryAsync(context, inventory, ct);
        context.WriteAgents(inventory.Agents);
        if (!context.Settings.CanAuthenticate(context.Store))
        {
            console.MarkupLine("[yellow]This machine isn't signed in yet. Run mbus login --server <url> so the agents can reach messagebus.ai.[/]");
        }
        return 0;
    }

    private static async Task<bool> TrySendInventoryAsync(CommandContext context, MachineInventory inventory, CancellationToken ct)
    {
        if (context.Settings.Server is not { } server || !context.Settings.CanAuthenticate(context.Store))
        {
            return false;
        }
        using var client = ServerConnection.CreateAuthenticatedClient(context.Host, context.Settings, context.Store, "cli");
        inventory = await MessageBus.Cli.Readiness.RoleKeyChecks.CheckAsync(context, inventory, ct);
        await ServerConnection.PutInventoryAsync(client, server, inventory, ct);
        return true;
    }

    private static AgentKind ParseAgent(string name) => name.Trim().ToLowerInvariant() switch
    {
        "claude" or "claude-code" or "claudecode" => AgentKind.ClaudeCode,
        "codex" => AgentKind.Codex,
        "copilot" => AgentKind.Copilot,
        "opencode" => AgentKind.OpenCode,
        _ => throw new CliException($"'{name}' isn't an agent mbus knows. Use claude, codex, copilot or opencode.", 2),
    };
}
