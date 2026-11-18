using System.CommandLine;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Contracts.Machines;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary>Options every command accepts (one set per command tree).</summary>
public sealed class CommonOptions
{
    /// <summary><c>--server &lt;url&gt;</c>: the server for this command (else <c>MBUS_SERVER</c>, else the stored one).</summary>
    public Option<string?> Server { get; } = new("--server")
    {
        Description = "Server address, for example https://app.messagebus.ai. Defaults to the one you last signed in to.",
        Recursive = true,
    };
}

/// <summary>What a command works with: the machine, the settings folder and the resolved settings.</summary>
public sealed class CommandContext
{
    private CommandContext(CliHost host, ConfigStore store, CliSettings settings)
    {
        Host = host;
        Store = store;
        Settings = settings;
    }

    /// <summary>The machine.</summary>
    public CliHost Host { get; }

    /// <summary>The settings folder.</summary>
    public ConfigStore Store { get; }

    /// <summary>Stored settings with environment variables and <c>--server</c> applied.</summary>
    public CliSettings Settings { get; }

    /// <summary>Loads settings for a command.</summary>
    public static CommandContext Load(CliHost host, ParseResult parse, CommonOptions options)
    {
        var store = new ConfigStore(host.ConfigDirectory, host.GetEnvironmentVariable(ConfigStore.KeyStoreVariable), host.GetEnvironmentVariable);
        return new CommandContext(host, store, CliSettings.Resolve(host, store.Load(), parse.GetValue(options.Server)));
    }

    /// <summary>A Spectre console over the host's output (plain text when it isn't a terminal).</summary>
    public IAnsiConsole Console => _console ??= CreateConsole(Host.Out);

    private IAnsiConsole? _console;

    private static IAnsiConsole CreateConsole(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.Detect,
            ColorSystem = ColorSystemSupport.Detect,
            Interactive = InteractionSupport.No,
        });
        if (!console.Profile.Out.IsTerminal)
        {
            // Piped or captured output: don't wrap long paths at 80 columns.
            console.Profile.Width = 1000;
        }
        return console;
    }

    /// <summary>The server URL as people write it (no trailing slash).</summary>
    public static string ServerText(Uri server) => server.AbsoluteUri.TrimEnd('/');

    /// <summary>Prints the agent scan as a table.</summary>
    public void WriteAgents(IReadOnlyList<DetectedAgent> agents)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Agent").AddColumn("Installed").AddColumn("Version").AddColumn("On PATH").AddColumn("Uses mbus").AddColumn("Signed in");
        foreach (var agent in agents)
        {
            table.AddRow(
                new Text(AgentScanner.DisplayName(agent.Kind)),
                YesNo(agent.Found),
                new Text(agent.Version ?? (agent.Found ? "unknown" : "")),
                agent.Found ? YesNo(agent.OnPath) : new Text(""),
                agent.Found ? YesNo(agent.McpConfigured) : new Text(""),
                agent.Found ? YesNo(agent.SignedIn) : new Text(""));
        }
        Console.Write(table);
        foreach (var agent in agents.Where(a => a is { Found: true, NotReady: not null }))
        {
            Console.MarkupLineInterpolated($"[yellow]{AgentScanner.DisplayName(agent.Kind)} can't take work yet:[/] {agent.NotReady}");
        }
    }

    private static Markup YesNo(bool value) => value ? new Markup("[green]yes[/]") : new Markup("[grey]no[/]");

    /// <summary>Asks a yes/no question on the host's input; anything but yes is no.</summary>
    public bool Confirm(string question)
    {
        Host.Out.Write($"{question} [y/N] ");
        Host.Out.Flush();
        var answer = Host.In.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
