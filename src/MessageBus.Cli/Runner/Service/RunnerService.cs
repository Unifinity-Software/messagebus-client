using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using MessageBus.Cli.Agents;

namespace MessageBus.Cli.Runner.Service;

/// <summary>Which service manager <c>mbus runner install</c> registers the runner with.</summary>
public enum ServicePlatform
{
    /// <summary>A per-user Scheduled Task that starts at sign-in (no administrator rights needed).</summary>
    Windows,

    /// <summary>A launchd agent in <c>~/Library/LaunchAgents</c>.</summary>
    MacOs,

    /// <summary>Not supported yet.</summary>
    Other,
}

/// <summary>One command <c>mbus runner install</c> or <c>uninstall</c> runs (<c>schtasks</c>, <c>launchctl</c>).</summary>
/// <param name="FileName">The program.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="MayFail">True when failing is expected and harmless (stopping something that isn't running).</param>
public sealed record ServiceStep(string FileName, IReadOnlyList<string> Arguments, bool MayFail = false)
{
    /// <inheritdoc />
    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Arguments).Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}

/// <summary>What installing (or removing) the runner as a service does: the definition file and the commands.</summary>
/// <param name="Platform">The service manager.</param>
/// <param name="Name">The task's or agent's name.</param>
/// <param name="DefinitionPath">Where the definition is written.</param>
/// <param name="Definition">Its contents.</param>
/// <param name="DefinitionEncoding">The encoding the service manager reads it in.</param>
/// <param name="LogPath">Where the runner's output goes.</param>
/// <param name="Install">Commands that register and start it, after the file is written.</param>
/// <param name="Uninstall">Commands that stop and remove it, before the file is deleted.</param>
/// <param name="Stop">The service manager's own stop: ends it now and leaves it registered (<c>mbus runner stop</c>, and install before it registers).</param>
/// <param name="Exists">
/// A command that succeeds only when it is registered and prints the registered definition (Windows); null where the
/// definition file is what is registered (macOS).
/// </param>
/// <param name="Disable">
/// Keeps a registered runner from being started again (Windows: its every-minute repeat and the next sign-in); null where
/// the stop request alone does that. Run by <c>mbus runner stop</c> only when <paramref name="Exists"/> finds it.
/// </param>
public sealed record RunnerServicePlan(ServicePlatform Platform, string Name, string DefinitionPath, string Definition, Encoding DefinitionEncoding, string LogPath,
    IReadOnlyList<ServiceStep> Install, IReadOnlyList<ServiceStep> Uninstall, IReadOnlyList<ServiceStep> Stop, ServiceStep? Exists = null, ServiceStep? Disable = null)
{
    /// <summary>Why tool updates cannot be used by this service definition.</summary>
    public string? UpdateUnavailableReason { get; init; }
}

/// <summary>
/// The machine's service manager as <c>mbus runner install</c> sees it: which one, who the user is, and how a
/// command runs. Tests give a fake one, so nothing is ever registered on the machine running them.
/// </summary>
/// <param name="platform">Which service manager this machine has.</param>
/// <param name="userName">The user the runner runs as.</param>
/// <param name="userId">The user's numeric id on macOS.</param>
/// <param name="run">Runs one step.</param>
/// <param name="openProcess">
/// Holds a process open to check and end it (the original issue, a runner that didn't stop when asked); null when it isn't running.
/// Null (tests) ends nothing.
/// </param>
/// <param name="processes">The machine's processes, to recognise a runner before ending it (the original issue); null (tests): none.</param>
public sealed class ServiceManager(ServicePlatform platform, string userName, uint userId, Func<ServiceStep, CancellationToken, Task<(int ExitCode, string Output)>> run,
    Func<int, HeldProcess?>? openProcess = null, ProcessTable? processes = null)
{
    /// <summary>The machine's processes.</summary>
    public ProcessTable Processes { get; } = processes ?? ProcessTable.Empty;

    /// <summary>Which service manager this machine has.</summary>
    public ServicePlatform Platform { get; } = platform;

    /// <summary>The user the runner runs as: <c>DOMAIN\name</c> on Windows.</summary>
    public string UserName { get; } = userName;

    /// <summary>The user's numeric id on macOS (launchd's <c>gui/&lt;uid&gt;</c> domain).</summary>
    public uint UserId { get; } = userId;

    /// <summary>Runs one step; returns its exit code and output.</summary>
    public Task<(int ExitCode, string Output)> RunAsync(ServiceStep step, CancellationToken ct) => run(step, ct);

    /// <summary>
    /// Ends <paramref name="expected"/> and everything it started, but only after reading it again through the held
    /// process that is then ended: still the same id, start time and file, and <paramref name="accept"/> (the runner's
    /// command line, the restart loop's shape). Anything else is <see cref="EndOutcome.Refused"/>.
    /// </summary>
    public EndOutcome EndProcessTree(ProcessIdentity expected, Func<ProcessRecord, bool> accept)
    {
        if (openProcess is null)
        {
            return EndOutcome.NotEnded;
        }
        using var held = openProcess(expected.Pid);
        if (held is null)
        {
            return EndOutcome.Gone;
        }
        if (held.Read() is not { } now)
        {
            return EndOutcome.NotEnded;
        }
        if (!RunnerProcesses.SameProcess(expected, now) || !accept(now))
        {
            return EndOutcome.Refused;
        }
        return held.EndTree() ? EndOutcome.Ended : EndOutcome.NotEnded;
    }

    /// <summary>This machine's.</summary>
    public static ServiceManager ForThisMachine()
    {
        var platform = OperatingSystem.IsWindows() ? ServicePlatform.Windows : OperatingSystem.IsMacOS() ? ServicePlatform.MacOs : ServicePlatform.Other;
        var user = OperatingSystem.IsWindows() ? $"{Environment.UserDomainName}\\{Environment.UserName}" : Environment.UserName;
        return new ServiceManager(platform, user, OperatingSystem.IsMacOS() ? GetUid() : 0, async (step, ct) =>
        {
            var result = await CommandRunner.RunAsync(step.FileName, step.Arguments, ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            return result is null ? (-1, $"{step.FileName} didn't start or didn't finish in a minute.") : (result.ExitCode, result.AllOutput.Trim());
        }, NativeProcesses.Open, ProcessTable.ForThisMachine());
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}

/// <summary>
/// The runner's service definitions (the original issue). Both start <c>mbus runner</c> when the user signs in, keep it running, and
/// start it again after it exits for an update (exit code 75, <see cref="RunnerCommand.UpdateExitCode"/>), so the new
/// version takes over:
/// <list type="bullet">
/// <item>Windows: a Scheduled Task for this user, triggered at sign-in, that never starts a second copy. Registering
/// one for yourself needs no administrator rights, and it runs in your session with your sign-ins (Claude Code, Codex,
/// git), which a Windows service running as LocalSystem wouldn't have. Task Scheduler doesn't restart a program that
/// exits with an error code, and the trigger's every-minute repeat only runs from a sign-in on (a task started by
/// <c>mbus runner install</c> or by hand has none until the next sign-in, which left a runner that exited for an
/// update stopped, the original issue). So the task's command is a loop: it starts the runner again a few seconds after any exit but
/// a clean stop (0), like launchd's KeepAlive; the repeat stays as a second way back. It runs without a window
/// (<c>conhost --headless</c>), its output appended to a log.</item>
/// <item>macOS: a launchd agent with a supervisor that restarts the runner after failures and updates without
/// exiting itself. This also works when the GUI domain defers KeepAlive respawns in on-demand-only mode.
/// A clean runner exit or stop request ends the supervisor.</item>
/// </list>
/// </summary>
public static class RunnerServiceDefinitions
{
    /// <summary>The Scheduled Task's name.</summary>
    public const string TaskName = "mbus-runner";

    /// <summary>The launchd agent's label.</summary>
    public const string LaunchdLabel = "ai.messagebus.runner";

    /// <summary>
    /// Set (to <c>1</c>) in the launchd agent's environment, so the runner knows a service manager started it, not a
    /// terminal or an SSH session, and says how to fix a sign-in for a service (the original issue).
    /// </summary>
    public const string ServiceVariable = "MBUS_RUNNER_SERVICE";

    /// <summary>How often Task Scheduler starts the runner again if it isn't running (its shortest repeat).</summary>
    public static readonly TimeSpan WindowsRestartInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long the Windows task waits before starting the runner again after it exits with a code other than 0.</summary>
    public const int WindowsRestartDelaySeconds = 5;

    private static readonly XNamespace TaskXml = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The plan for <paramref name="manager"/>'s platform.</summary>
    /// <param name="manager">The service manager.</param>
    /// <param name="mbus">The command that starts this mbus (its executable, then any arguments before the command).</param>
    /// <param name="runnerArgs">Arguments after <c>runner</c> (<c>--slots</c>, <c>--work-dir</c>).</param>
    /// <param name="home">The user's home folder.</param>
    /// <param name="configDirectory">mbus's settings folder (the log goes in it).</param>
    /// <param name="environment">
    /// Variables to give the runner (launchd starts it with almost none). Never secrets: the definition is readable by
    /// every user of the machine; agent sign-ins are kept in <see cref="AgentEnvironmentStore"/> instead (the original issue).
    /// </param>
    public static RunnerServicePlan Plan(ServiceManager manager, IReadOnlyList<string> mbus, IReadOnlyList<string> runnerArgs, string home, string configDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var log = Path.Combine(configDirectory, "logs", "runner.log");
        // the original issue: the runner writes (and rotates) its own log; the service manager's redirect only catches what comes
        // before that, or a crash.
        var console = Path.Combine(configDirectory, "logs", "runner-errors.log");
        IReadOnlyList<string> command = [.. mbus, "runner", .. runnerArgs, "--log-file", log];
        return manager.Platform switch
        {
            ServicePlatform.Windows => Windows(manager.UserName, command, home, configDirectory, log, console),
            ServicePlatform.MacOs => MacOs(manager.UserId, command, home, configDirectory, log, console, environment),
            _ => throw new CliException("mbus runner install supports Windows and macOS. On Linux, run mbus runner from systemd (a user unit with Restart=on-failure)."),
        };
    }

    /// <summary>Whether <c>mbus runner install</c> has registered the runner on this machine (its definition file is there).</summary>
    /// <param name="home">The user's home folder.</param>
    /// <param name="configDirectory">mbus's settings folder.</param>
    public static bool IsInstalled(string home, string configDirectory) =>
        OperatingSystem.IsWindows() ? File.Exists(Path.Combine(configDirectory, "service", TaskName + ".xml"))
        : OperatingSystem.IsMacOS() && File.Exists(Path.Combine(home, "Library", "LaunchAgents", LaunchdLabel + ".plist"));

    private static RunnerServicePlan Windows(string user, IReadOnlyList<string> command, string home, string configDirectory, string log, string console)
    {
        // Windows paths are written as Windows writes them, whatever machine builds the plan.
        var system = (Environment.GetFolderPath(Environment.SpecialFolder.System) is { Length: > 0 } dir ? dir : @"C:\Windows\System32").TrimEnd('\\');
        // cmd /s /c "<line>": cmd drops the outer quotes and keeps the rest as written. The line is a loop without an
        // end (for /l with no range): run the runner; a clean stop (0) ends the task, anything else (75 after an update, 1
        // when the connection gave up, a crash) waits a few seconds (ping, which needs no console input) and runs it again.
        // Ending the task (schtasks /End, mbus runner uninstall) ends the loop with it. Every path is quoted: a bare
        // parenthesis would end the loop's block.
        var runner = string.Join(' ', command.Select(QuoteAlways)) + $" >> {QuoteAlways(console)} 2>&1";
        var hook = Path.Combine(configDirectory, "update", "wrapper.cmd");
        string? updateUnavailable = null;
        try { Update.DotnetToolUpdater.ValidatePaths(true, hook); }
        catch (IOException ex) { updateUnavailable = ex.Message; }
        var step = updateUnavailable is null ? $"(if exist {QuoteAlways(hook)} call {QuoteAlways(hook)}) & " : "";
        var line = $"for /l %i in () do ({step}{runner} && exit 0 & {QuoteAlways(system + @"\ping.exe")} -n {WindowsRestartDelaySeconds + 1} 127.0.0.1 >nul)";
        var arguments = $"--headless {Quote(system + @"\cmd.exe")} /d /s /c \"{line}\"";
        var task = new XDocument(new XDeclaration("1.0", "UTF-16", null),
            new XElement(TaskXml + "Task", new XAttribute("version", "1.2"),
                new XElement(TaskXml + "RegistrationInfo",
                    new XElement(TaskXml + "Description", "Keeps this computer available for messagebus.ai work (mbus runner). Added by mbus runner install; remove it with mbus runner uninstall."),
                    new XElement(TaskXml + "URI", "\\" + TaskName)),
                new XElement(TaskXml + "Triggers",
                    new XElement(TaskXml + "LogonTrigger",
                        new XElement(TaskXml + "Repetition",
                            new XElement(TaskXml + "Interval", Duration(WindowsRestartInterval)),
                            new XElement(TaskXml + "StopAtDurationEnd", "false")),
                        new XElement(TaskXml + "Enabled", "true"),
                        new XElement(TaskXml + "UserId", user))),
                new XElement(TaskXml + "Principals",
                    new XElement(TaskXml + "Principal", new XAttribute("id", "Author"),
                        new XElement(TaskXml + "UserId", user),
                        new XElement(TaskXml + "LogonType", "InteractiveToken"),
                        new XElement(TaskXml + "RunLevel", "LeastPrivilege"))),
                new XElement(TaskXml + "Settings",
                    new XElement(TaskXml + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskXml + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskXml + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskXml + "AllowHardTerminate", "true"),
                    new XElement(TaskXml + "StartWhenAvailable", "true"),
                    new XElement(TaskXml + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(TaskXml + "IdleSettings",
                        new XElement(TaskXml + "StopOnIdleEnd", "false"),
                        new XElement(TaskXml + "RestartOnIdle", "false")),
                    new XElement(TaskXml + "AllowStartOnDemand", "true"),
                    new XElement(TaskXml + "Enabled", "true"),
                    new XElement(TaskXml + "Hidden", "false"),
                    new XElement(TaskXml + "RunOnlyIfIdle", "false"),
                    new XElement(TaskXml + "WakeToRun", "false"),
                    new XElement(TaskXml + "ExecutionTimeLimit", "PT0S"),
                    new XElement(TaskXml + "Priority", "7")),
                new XElement(TaskXml + "Actions", new XAttribute("Context", "Author"),
                    new XElement(TaskXml + "Exec",
                        new XElement(TaskXml + "Command", system + @"\conhost.exe"),
                        new XElement(TaskXml + "Arguments", arguments),
                        new XElement(TaskXml + "WorkingDirectory", home)))));

        var definition = Path.Combine(configDirectory, "service", TaskName + ".xml");
        var schtasks = system + @"\schtasks.exe";
        return new RunnerServicePlan(ServicePlatform.Windows, TaskName, definition, task.Declaration + Environment.NewLine + task.Root, Encoding.Unicode, log,
            Install:
            [
                new ServiceStep(schtasks, ["/End", "/TN", TaskName], MayFail: true),
                new ServiceStep(schtasks, ["/Create", "/TN", TaskName, "/XML", definition, "/F"]),
                new ServiceStep(schtasks, ["/Run", "/TN", TaskName]),
            ],
            Uninstall:
            [
                new ServiceStep(schtasks, ["/End", "/TN", TaskName], MayFail: true),
                new ServiceStep(schtasks, ["/Delete", "/TN", TaskName, "/F"]),
            ],
            Stop: [new ServiceStep(schtasks, ["/End", "/TN", TaskName], MayFail: true)],
            // Its output is the registered task, so mbus can tell whose settings folder it runs from (the original issue).
            Exists: new ServiceStep(schtasks, ["/Query", "/TN", TaskName, "/XML"], MayFail: true),
            // mbus runner stop disables it too, or its every-minute repeat starts it again (install's /Create enables it again).
            Disable: new ServiceStep(schtasks, ["/Change", "/TN", TaskName, "/Disable"])) { UpdateUnavailableReason = updateUnavailable };
    }

    private static RunnerServicePlan MacOs(uint uid, IReadOnlyList<string> command, string home, string configDirectory, string log, string console, IReadOnlyDictionary<string, string> environment)
    {
        static XElement Key(string name) => new("key", name);
        static XElement Text(string value) => new("string", value);
        var env = new XElement("dict");
        foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            env.Add(Key(name), Text(value));
        }
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("dict",
                    Key("Label"), Text(LaunchdLabel),
                    Key("ProgramArguments"), new XElement("array", new[] { "/bin/sh", "-c",
                        MacRunnerSupervisor.Script(configDirectory), MacRunnerSupervisor.ArgumentMarker }.Concat(command).Select(Text)),
                    Key("RunAtLoad"), new XElement("true"),
                    // Restart after any exit but a clean stop (0): an update (75) or a connection that gave up (1).
                    Key("KeepAlive"), new XElement("dict", Key("SuccessfulExit"), new XElement("false")),
                    Key("ThrottleInterval"), new XElement("integer", "10"),
                    Key("WorkingDirectory"), Text(home),
                    Key("EnvironmentVariables"), env,
                    Key("StandardOutPath"), Text(console),
                    Key("StandardErrorPath"), Text(console))));

        var definition = Path.Combine(home, "Library", "LaunchAgents", LaunchdLabel + ".plist");
        const string launchctl = "/bin/launchctl";
        var domain = $"gui/{uid}";
        return new RunnerServicePlan(ServicePlatform.MacOs, LaunchdLabel, definition, plist.Declaration + "\n" + plist.DocumentType + "\n" + plist.Root, new UTF8Encoding(false), log,
            Install:
            [
                new ServiceStep(launchctl, ["bootout", $"{domain}/{LaunchdLabel}"], MayFail: true),
                new ServiceStep(launchctl, ["bootstrap", domain, definition]),
                // RunAtLoad can be deferred when bootstrap comes from SSH into the GUI domain (the original issue).
                new ServiceStep(launchctl, ["kickstart", $"{domain}/{LaunchdLabel}"]),
            ],
            Uninstall:
            [
                new ServiceStep(launchctl, ["bootout", $"{domain}/{LaunchdLabel}"], MayFail: true),
            ],
            // The plist stays, so it loads again at the next sign-in, and the runner it starts sees the stop request.
            Stop: [new ServiceStep(launchctl, ["bootout", $"{domain}/{LaunchdLabel}"], MayFail: true)]);
    }

    private static string Quote(string value) => value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^')
        ? value
        : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Quoted unless it is a plain switch or number (<c>--slots</c>, <c>2</c>), so no path can end the loop's block.</summary>
    private static string QuoteAlways(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        ? value
        : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Duration(TimeSpan value) => System.Xml.XmlConvert.ToString(value);
}
