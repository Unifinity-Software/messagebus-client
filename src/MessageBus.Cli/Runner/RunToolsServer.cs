using System.Diagnostics;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Mcp;

namespace MessageBus.Cli.Runner;

/// <summary>
/// The runner's side of each run's mbus tools (the original issue): the same local MCP server <c>mbus mcp</c> runs, signed in as
/// this machine, but limited to the run's task (<see cref="RunToolScope"/>), working in the run's folder and starting
/// git inside the run's sandbox. The agent reaches it through <see cref="PipeRunTools"/> with the run's credential.
/// </summary>
internal static class RunToolsServer
{
    private static readonly HttpClient Links = MessageBus.Attachments.Client.AttachmentTransfers.CreateHttpClient();

    /// <summary>The run tools for this machine; null when it has no workspace to serve tools in.</summary>
    public static IRunTools? Create(CommandContext context)
    {
        if (context.Settings.Stored.WorkspaceId is not { } workspace || context.Settings.Server is null)
        {
            return null;
        }
        var host = context.Host;
        // One client for every run's tools, signed as this machine (the runner process's lifetime).
        var http = ServerConnection.CreateAuthenticatedClient(host, context.Settings, context.Store, "mcp");
        var git = ExecutableLocator.Find(host, "git")?.Path;
        LocalServerOptions Options(RunWorkspace run) => LocalServer.Options(http, workspace, context.Settings.Stored.MachineId,
            context.Store.Directory, Stream.Null, Stream.Null, host.Error, CliHost.Version, host.GetEnvironmentVariable) with
        {
            Links = Links,
            MachineKeyPem = context.Store.LoadKeyPem,
            WorkingDirectory = run.WorkTree,
            Time = host.Time,
            TempDirectory = Directory.CreateDirectory(Path.Combine(run.Root, "tmp")).FullName,
        };
        return new PipeRunTools(async (request, stream, ct) =>
        {
            var run = request.Workspace;
            // The runner's environment for the session logs and call limits; the task's own files and folders on top.
            var options = Options(run) with
            {
                Input = stream,
                Output = stream,
                DeliveryFile = run.DeliveryFile,
                Delivery = request.Delivery,
                SettleFile = run.SettleFile,
                Agent = request.Delivery.AgentId,
                Scope = RunToolScope.For(request.Delivery, run.Root) with { ProposalAuthority = request.ProposalAuthority },
                ProposalAuthority = request.ProposalAuthority,
                Refused = request.Refused,
                RecordVerifiedTaskSettle = request.RecordVerifiedTaskSettle,
                RecordStepOutcome = request.RecordStepOutcome,
                FullBundleFriction = run.FullBundleFriction,
                PrepareGit = start => InRun(start, request, git),
                Session = request.Session,
            };
            await LocalServer.RunAsync(request.WithExhibits(options), ct).ConfigureAwait(false);
        }, host.Error, (run, delivery) => Exhibits.Create(Options(run), delivery.StepContext, run.WorkTree));
    }

    /// <summary>
    /// Sends a run's kept answer once its agent has ended (the original issue; <see cref="LocalServer.SendKeptAnswerAsync"/>), as this machine,
    /// whether or not the runner served the run's tools; null when this machine has no workspace.
    /// </summary>
    public static Func<RunWorkspace, Contracts.Runner.TaskDelivery, CancellationToken, Task<bool>>? KeptAnswerSender(CommandContext context)
    {
        if (context.Settings.Stored.WorkspaceId is not { } workspace || context.Settings.Server is null)
        {
            return null;
        }
        var host = context.Host;
        var http = ServerConnection.CreateAuthenticatedClient(host, context.Settings, context.Store, "mcp");
        return (run, delivery, ct) => LocalServer.SendKeptAnswerAsync(LocalServer.Options(http, workspace, context.Settings.Stored.MachineId,
            context.Store.Directory, Stream.Null, Stream.Null, host.Error, CliHost.Version, host.GetEnvironmentVariable) with
        {
            Links = Links,
            MachineKeyPem = context.Store.LoadKeyPem,
            DeliveryFile = run.DeliveryFile,
            Delivery = delivery,
            SettleFile = run.SettleFile,
            Agent = delivery.AgentId,
            WorkingDirectory = run.WorkTree,
            Time = host.Time,
            Scope = RunToolScope.For(delivery, run.Root),
            TempDirectory = Directory.CreateDirectory(Path.Combine(run.Root, "tmp")).FullName,
        }, ct);
    }

    /// <summary>A git the run's tools start, as the run would start it: its environment, git settings and (macOS) sandbox.</summary>
    internal static void InRun(ProcessStartInfo start, RunToolsRequest request, string? git)
    {
        var isGit = start.FileName == "git" || Path.GetFileNameWithoutExtension(start.FileName).Equals("git", StringComparison.OrdinalIgnoreCase);
        var fileName = isGit && git is not null ? git : start.FileName;
        var launch = request.Sandboxed(new ProcessLaunch
        {
            FileName = fileName,
            // No hooks or fsmonitor from the run's clone: the runner packages it as this machine (as RepoMirrors does).
            Arguments = isGit
                ? RunnerGit.Arguments(start.ArgumentList)
                : [.. start.ArgumentList],
            WorkingDirectory = start.WorkingDirectory,
            Environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never", ["GIT_EDITOR"] = "true", ["LC_ALL"] = "C", ["LANGUAGE"] = "C" },
        });
        start.FileName = launch.FileName;
        start.ArgumentList.Clear();
        foreach (var arg in launch.Arguments)
        {
            start.ArgumentList.Add(arg);
        }
        foreach (var (name, value) in launch.Environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }
        CodexAuthentication.RemoveKeys(start.Environment);
    }
}
