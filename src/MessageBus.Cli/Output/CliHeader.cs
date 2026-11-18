using System.CommandLine;
using System.CommandLine.Parsing;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using Spectre.Console;

namespace MessageBus.Cli.Output;

/// <summary>
/// The line an interactive command starts with (the original issue): <c>mbus 0.4.0 · messagebus.contoso.com · Mac mini · Contoso</c>,
/// so a person can see which mbus they ran, against which server, as which machine and in which workspace.
/// </summary>
public static class CliHeader
{
    /// <summary>
    /// Commands that never get the header: <c>mcp</c> speaks the MCP protocol on stdout, and <c>runner</c> and
    /// <c>doctor</c> start with a fuller summary of the same facts.
    /// </summary>
    private static readonly HashSet<string> OwnSummary = new(StringComparer.Ordinal) { "mcp", "runner", "doctor" };

    /// <summary>
    /// Whether <paramref name="parse"/> gets the header: only when stdout is a terminal, the command line parsed and runs
    /// a command's own action (not <c>--help</c>, <c>--version</c> or a parse error), the command isn't one of
    /// <see cref="OwnSummary"/> (or under <c>mcp</c>), and it wasn't asked for <c>--json</c>.
    /// </summary>
    public static bool ShouldShow(ParseResult parse, bool outIsTerminal)
    {
        if (!outIsTerminal || parse.Errors.Count > 0)
        {
            return false;
        }
        var command = parse.CommandResult.Command;
        if (command is RootCommand || command.Action is null || !ReferenceEquals(parse.Action, command.Action))
        {
            return false;
        }
        if (OwnSummary.Contains(command.Name) || TopLevel(parse.CommandResult)?.Command.Name == "mcp")
        {
            return false;
        }
        return !command.Options.Any(o => o.Name == "--json" && o is Option<bool> json && parse.GetValue(json));
    }

    /// <summary>The header's text: the version and whichever of server, machine and workspace are known.</summary>
    public static string Text(CliSettings? settings)
    {
        var parts = new List<string> { $"mbus {CliHost.Version}" };
        if (settings?.Server is { } server)
        {
            parts.Add(server.IsDefaultPort ? server.Host : server.Authority);
        }
        if (settings?.MachineName is { Length: > 0 } machine)
        {
            parts.Add(machine);
        }
        if ((settings?.Stored.WorkspaceName ?? settings?.Stored.WorkspaceId) is { Length: > 0 } workspace)
        {
            parts.Add(workspace);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Prints the header for <paramref name="parse"/> when <see cref="ShouldShow"/> says so. Never fails the command.</summary>
    public static void WriteIfShown(ParseResult parse, CliHost host)
    {
        if (!ShouldShow(parse, host.OutIsTerminal))
        {
            return;
        }
        CliSettings? settings = null;
        try
        {
            var store = new ConfigStore(host.ConfigDirectory, host.GetEnvironmentVariable(ConfigStore.KeyStoreVariable), host.GetEnvironmentVariable);
            settings = CliSettings.Resolve(host, store.Load(), parse.GetValue<string?>("--server"));
        }
        catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or UriFormatException)
        {
            // Unreadable settings: the command itself says what is wrong; the header shows what it can.
        }
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(host.Out),
            Ansi = AnsiSupport.Detect,
            ColorSystem = ColorSystemSupport.Detect,
            Interactive = InteractionSupport.No,
        });
        console.MarkupLine($"[grey]{Markup.Escape(Text(settings))}[/]");
    }

    private static CommandResult? TopLevel(CommandResult result)
    {
        var current = result;
        while (current.Parent is CommandResult parent && parent.Command is not RootCommand)
        {
            current = parent;
        }
        return current.Command is RootCommand ? null : current;
    }
}
