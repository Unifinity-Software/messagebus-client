using System.CommandLine;
using System.CommandLine.Invocation;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Update;

namespace MessageBus.Cli;

/// <summary>
/// The <c>mbus</c> command tree. <see cref="RunAsync"/> is the whole program: MbusProgram.RunAsync calls it
/// with the real machine, tests call it with a <see cref="CliHost"/> over temp folders and a fake
/// server.
/// </summary>
public static partial class CliApp
{
    /// <summary>Runs one mbus command line and returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        var root = BuildRootCommand(host);
        var configuration = new InvocationConfiguration
        {
            Output = host.Out,
            Error = host.Error,
            EnableDefaultExceptionHandler = false,
        };
        try
        {
            var parse = root.Parse(args);
            // the original issue: which mbus, server, machine and workspace, for a person at a terminal (never on MCP's or --json's stdout).
            Output.CliHeader.WriteIfShown(parse, host);
            return await parse.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            await host.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ex.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }

    /// <summary>The command tree.</summary>
    public static RootCommand BuildRootCommand(CliHost host)
    {
        var options = new CommonOptions();
        var root = new RootCommand("mbus connects this machine's coding agents to a messagebus.ai server.");
        // System.CommandLine defaults to the entry assembly; mbus's version belongs to this library.
        root.Options.OfType<VersionOption>().Single().Action = new VersionAction(host.Out);
        root.Options.Add(options.Server);
        root.Subcommands.Add(LoginCommand.Create(host, options));
        root.Subcommands.Add(ConnectCommand.Create(host, options));
        root.Subcommands.Add(DoctorCommand.Create(host, options));
        root.Subcommands.Add(McpCommand.Create(host, options));
        root.Subcommands.Add(MachineCommand.Create(host, options));
        root.Subcommands.Add(LogoutCommand.Create(host, options));
        root.Subcommands.Add(UpdateCommand.Create(host, options));
        root.Subcommands.Add(Keys.KeysCommand.Create(host, options));
        root.Subcommands.Add(Keys.DeadLettersCommand.Create(host, options));
        Scripts.ScriptCommands.AddTo(root, host, options);
        root.Subcommands.Add(JobCommand.Create(host, options));
        root.Subcommands.Add(WorkflowCommand.Create(host, options));
        Approvals.ApprovalsCommand.AddTo(root, host, options);
        AddRunnerCommands(root, host, options);
        return root;
    }

    private sealed class VersionAction(TextWriter output) : SynchronousCommandLineAction
    {
        public override bool Terminating => true;
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            output.WriteLine(CliHost.Version);
            return 0;
        }
    }

    /// <summary>
    /// Integration point for the original issue: implement this partial method in a file under
    /// <c>src/MessageBus.Cli/Runner/</c> to add <c>mbus runner</c> without editing this file:
    /// <code>
    /// public static partial class CliApp
    /// {
    ///     static partial void AddRunnerCommands(RootCommand root, CliHost host, CommonOptions options) =>
    ///         root.Subcommands.Add(RunnerCommand.Create(host, options));
    /// }
    /// </code>
    /// In the command's action, <c>CommandContext.Load(host, parse, options)</c> gives the settings,
    /// and <c>ServerConnection.CreateAuthenticatedClient</c> / <c>CreateSigner</c> the server.
    /// </summary>
    static partial void AddRunnerCommands(RootCommand root, CliHost host, CommonOptions options);
}
