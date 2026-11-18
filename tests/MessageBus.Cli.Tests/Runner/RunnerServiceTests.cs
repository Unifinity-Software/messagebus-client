using System.Xml.Linq;
using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Tests.Infrastructure;
using Xunit;
using MessageBus.Tests.Shared;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>
/// <c>mbus runner install</c> / <c>uninstall</c> (the original issue) through the real command, with a fake service manager: what is
/// registered must start the runner at sign-in and bring it back after an update drain (exit 75), and uninstall must
/// remove it. Nothing is registered on the machine running the tests.
/// </summary>
public sealed class RunnerServiceTests
{
    [Theory]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("^")]
    public void Unsafe_update_settings_path_still_allows_service_plan(string character)
    {
        var manager = new ServiceManager(ServicePlatform.Windows, "dev", 0, (_, _) => throw new Exception("Plan only"));
        var plan = RunnerServiceDefinitions.Plan(manager, ["mbus"], [], "/home/dev",
            "/settings" + character, new Dictionary<string, string>());
        Assert.Contains("cannot safely use", plan.UpdateUnavailableReason);
        Assert.DoesNotContain("wrapper.cmd", plan.Definition);
        Assert.Contains("runner", plan.Definition);
    }

    [Fact]
    public async Task Install_inside_a_run_refuses_before_reading_settings_or_stopping_services()
    {
        using var machine = new TestMachine();
        machine.Env[MessageBus.Cli.Runner.RunToolsVariables.File] = "run-tools.json";
        Directory.CreateDirectory(machine.MbusFolder);
        File.WriteAllText(Path.Combine(machine.MbusFolder, "config.json"), "invalid json");
        machine.RunnerServices = () => throw new InvalidOperationException("Must not touch the runner.");
        var result = await machine.RunAsync("runner", "install");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(MessageBus.Cli.Security.RunContext.KeyRefused, result.Error);
    }

    [Fact]
    public async Task Uninstall_preserves_the_machine_identity_used_by_other_commands()
    {
        using var machine = new TestMachine();
        machine.Env["MBUS_KEY_STORE"] = "file";
        using var key = MessageBus.Cli.Security.MachineKey.Create();
        var store = new MessageBus.Cli.Configuration.ConfigStore(machine.MbusFolder, "file");
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, "dev", 0, (_, _) => Task.FromResult((1, "Not installed")));
        var result = await machine.RunAsync("runner", "uninstall");
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal(key.ExportPrivateKeyPem(), store.LoadKeyPem());
    }

    [Theory]
    [InlineData("always", null, "always")]
    [InlineData("unelevated", null, "unelevated")]
    [InlineData("invalid", "always", "always")]
    public async Task Install_print_persists_codex_policy_from_environment_or_flag(string environment, string? flag, string expected)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        SignIn(machine);
        machine.Env[MessageBus.Cli.Runner.Agents.CodexAdapter.SandboxOverrideVariable] = environment;
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, "dev", 0, (_, _) => throw new InvalidOperationException("Print must not register a service."));
        var args = new List<string> { "runner", "install", "--print" };
        if (flag is not null) args.AddRange(["--codex-sandbox", flag]);
        var result = await machine.RunAsync(args.ToArray());
        Assert.True(result.ExitCode == 0, result.Error);
        var start = result.Output.IndexOf("<Task ", StringComparison.Ordinal);
        var end = result.Output.IndexOf("</Task>", StringComparison.Ordinal) + "</Task>".Length;
        var definition = XDocument.Parse(result.Output[start..end]);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var command = definition.Descendants(ns + "Arguments").Single().Value;
        Assert.Contains($"--codex-sandbox {expected}", command);
    }

    [Theory]
    [InlineData("--keep-awake", "MBUS_RUNNER_KEEP_AWAKE", "off")]
    [InlineData("--codex-sandbox", "MBUS_RUNNER_CODEX_SANDBOX", "always")]
    [InlineData("--agent-tools", "MBUS_RUNNER_AGENT_TOOLS", "refuse")]
    public async Task Invalid_runner_policy_is_readable_for_flags_install_and_logged_startup(string option, string variable, string validValue)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        foreach (var args in new[] { new[] { "runner", option, "invalid" }, new[] { "runner", "install", "--print", option, "invalid" } })
        {
            var invalidFlag = await machine.RunAsync(args);
            Assert.NotEqual(0, invalidFlag.ExitCode);
            Assert.Contains(option, invalidFlag.Error);
            Assert.DoesNotContain("FormatException", invalidFlag.Error);
        }
        SignIn(machine);
        machine.Env[variable] = "invalid";
        var install = await machine.RunAsync("runner", "install", "--print");
        Assert.NotEqual(0, install.ExitCode);
        Assert.Contains(variable, install.Error);
        Assert.DoesNotContain("FormatException", install.Error);
        var log = Path.Combine(machine.MbusFolder, "logs", "runner.log");
        var startup = await machine.RunAsync("runner", "--log-file", log);
        Assert.NotEqual(0, startup.ExitCode);
        Assert.Contains(variable, File.ReadAllText(log));
        Assert.DoesNotContain("FormatException", File.ReadAllText(log));
        machine.Env[variable] = validValue;
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, "dev", 0, (_, _) => throw new InvalidOperationException("Print must not register a service."));
        var validInstall = await machine.RunAsync("runner", "install", "--print");
        Assert.True(validInstall.ExitCode == 0, validInstall.Error);
        Assert.Contains($"{option} {validValue}", validInstall.Output);
    }

    [Theory]
    [InlineData("tasks", null, "tasks")]
    [InlineData("tasks", "off", "off")]
    public async Task Install_keeps_sleep_policy_from_environment_or_flag(string environment, string? flag, string expected)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        SignIn(machine);
        machine.Env[MessageBus.Cli.Runner.RunnerEnvVars.KeepAwake] = environment;
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501, (_, _) => Task.FromResult((0, "")));
        var args = new List<string> { "runner", "install", "--print" };
        if (flag is not null) args.AddRange(["--keep-awake", flag]);
        var result = await machine.RunAsync(args.ToArray());
        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("--keep-awake", result.Output);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task File_choice_is_persisted_and_passed_to_launchd_without_touching_the_keychain()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The key-store install option is for macOS.");
        using var machine = new TestMachine();
        SignIn(machine);
        using var running = new ServiceRunner(machine);
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501,
            (step, _) => Task.FromResult(running.Run(step)));
        var preview = await machine.RunAsync("runner", "install", "--key-store", "file", "--print");
        Assert.Equal(0, preview.ExitCode);
        Assert.False(File.Exists(Path.Combine(machine.MbusFolder, "config.json")));
        var install = await machine.RunAsync("runner", "install", "--key-store", "file");
        Assert.True(install.ExitCode == 0, install.Error);
        Assert.Contains("key in a file only your account can read, not the Keychain, because --key-store file was requested", install.Output);
        var store = new MessageBus.Cli.Configuration.ConfigStore(machine.MbusFolder, "os");
        Assert.Equal("file", store.KeyStoreName);
        var file = Path.Combine(machine.Home, "Library", "LaunchAgents", RunnerServiceDefinitions.LaunchdLabel + ".plist");
        var plist = Plist(XDocument.Load(file).Root!.Element("dict")!);
        var environment = Plist((XElement)plist["EnvironmentVariables"]);
        Assert.Equal("file", ((XElement)environment[MessageBus.Cli.Configuration.ConfigStore.KeyStoreVariable]).Value);
    }

    [Fact]
    public async Task On_Windows_install_registers_a_sign_in_task_that_restarts_the_runner_and_uninstall_removes_it()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        var ran = new List<ServiceStep>();
        var file = Path.Combine(machine.MbusFolder, "service", RunnerServiceDefinitions.TaskName + ".xml");
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, @"CONTOSO\dev", 0, (step, _) =>
        {
            ran.Add(step);
            return Task.FromResult(step.Arguments[0] switch
            {
                "/Query" => File.Exists(file) ? (0, File.ReadAllText(file)) : (1, "ERROR: The system cannot find the file specified."),
                "/End" => (1, ""),
                _ => (0, ""),
            });
        });

        // Not signed in: a service that can't connect would only start and fail, so nothing is registered.
        Assert.NotEqual(0, (await machine.RunAsync("runner", "install")).ExitCode);
        Assert.Empty(ran);

        SignIn(machine);
        var install = await machine.RunAsync("runner", "install");
        Assert.True(install.ExitCode == 0, install.Error);

        var task = XDocument.Load(file).Root!;
        XNamespace t = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        // Yours, at your sign-in, without administrator rights.
        var logon = task.Element(t + "Triggers")!.Element(t + "LogonTrigger")!;
        Assert.Equal(@"CONTOSO\dev", logon.Element(t + "UserId")!.Value);
        var principal = task.Element(t + "Principals")!.Element(t + "Principal")!;
        Assert.Equal(("InteractiveToken", "LeastPrivilege"), (principal.Element(t + "LogonType")!.Value, principal.Element(t + "RunLevel")!.Value));
        // After it exits (an update drain), the next repeat starts it again within a minute; never two at once, never stopped for running long.
        Assert.Equal("PT1M", logon.Element(t + "Repetition")!.Element(t + "Interval")!.Value);
        var settings = task.Element(t + "Settings")!;
        Assert.Equal(("IgnoreNew", "PT0S"), (settings.Element(t + "MultipleInstancesPolicy")!.Value, settings.Element(t + "ExecutionTimeLimit")!.Value));
        var arguments = task.Element(t + "Actions")!.Element(t + "Exec")!.Element(t + "Arguments")!.Value;
        // the original issue: the runner keeps (and rotates) its own log; the redirect only catches a crash.
        var logs = Path.Combine(machine.MbusFolder, "logs");
        var runner = $"\"{machine.MbusCommand.Command}\" runner --log-file \"{Path.Combine(logs, "runner.log")}\" >> \"{Path.Combine(logs, "runner-errors.log")}\" 2>&1";
        Assert.Contains(runner, arguments);
        Assert.StartsWith("--headless ", arguments);
        // the original issue: the task itself starts the runner again after any exit but a clean stop (an update's 75, a crash); the
        // trigger's repeat alone doesn't run for a task started outside a sign-in.
        Assert.Contains($"{runner} && exit 0 & ", arguments);
        Assert.Contains("wrapper.cmd", arguments);

        // the original issue: nothing was registered to stop first; then it is registered.
        Assert.Equal(["/Query", "/End", "/Create", "/Run"], ran.Select(s => s.Arguments[0]));
        Assert.Contains(file, ran[2].Arguments);

        ran.Clear();
        var uninstall = await machine.RunAsync("runner", "uninstall");
        Assert.True(uninstall.ExitCode == 0, uninstall.Error);
        Assert.Equal(["/Query", "/End", "/Delete"], ran.Select(s => s.Arguments[0]));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task On_macOS_install_loads_a_launchd_agent_that_restarts_after_any_exit_but_a_clean_stop()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        SignIn(machine);
        machine.Env["PATH"] = "/opt/homebrew/bin:/usr/bin:/bin";
        // the original issue: the installing shell has claude setup-token's token and an OpenAI key; the service wouldn't get them.
        const string token = "sk-ant-oat01-from-the-installing-shell", openAi = "sk-proj-from-the-installing-shell";
        machine.Env["CLAUDE_CODE_OAUTH_TOKEN"] = token;
        machine.Env["OPENAI_API_KEY"] = openAi;
        var ran = new List<ServiceStep>();
        using var running = new ServiceRunner(machine);
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501, (step, _) =>
        {
            ran.Add(step);
            return Task.FromResult(running.Run(step));
        });

        var install = await machine.RunAsync("runner", "install");
        Assert.True(install.ExitCode == 0, install.Error);
        Assert.Contains("The runner has started", install.Output);

        var file = Path.Combine(machine.Home, "Library", "LaunchAgents", RunnerServiceDefinitions.LaunchdLabel + ".plist");
        var plist = Plist(XDocument.Load(file).Root!.Element("dict")!);
        Assert.Equal([machine.MbusCommand.Command, "runner", "--log-file", Path.Combine(machine.MbusFolder, "logs", "runner.log")],
            RunnerServiceCommand.RegisteredCommand(ServicePlatform.MacOs, File.ReadAllText(file)));
        Assert.Equal("true", ((XElement)plist["RunAtLoad"]).Name.LocalName);
        // Exit 75 (updated) or 1 (gave up) starts it again; 0 (stopped on purpose, e.g. by uninstall) doesn't.
        var keepAlive = Plist((XElement)plist["KeepAlive"]);
        Assert.Equal("false", ((XElement)keepAlive["SuccessfulExit"]).Name.LocalName);
        // launchd starts agents with a bare PATH: the runner gets the one mbus was installed from.
        var environment = Plist((XElement)plist["EnvironmentVariables"]);
        Assert.Equal("/opt/homebrew/bin:/usr/bin:/bin", ((XElement)environment["PATH"]).Value);
        Assert.True(environment.ContainsKey(RunnerServiceDefinitions.ServiceVariable));
        if (OperatingSystem.IsMacOS())
        {
            // TestMachine selects file storage to keep tests out of the real login Keychain.
            Assert.Equal("file", ((XElement)environment[MessageBus.Cli.Configuration.ConfigStore.KeyStoreVariable]).Value);
            Assert.Equal("file", new MessageBus.Cli.Configuration.ConfigStore(machine.MbusFolder, "file").Load().KeyStore);
            Assert.Contains("key in a file only your account can read, not the Keychain, because MBUS_KEY_STORE=file is set in this session", install.Output);
        }

        // the original issue: the sign-ins are kept where only this user can read them, never in the plist (every user can read that),
        // and only their names are printed.
        var definition = File.ReadAllText(file);
        Assert.DoesNotContain(token, definition);
        Assert.DoesNotContain(openAi, definition);
        Assert.Equal(new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token, ["OPENAI_API_KEY"] = openAi }, AgentEnvironmentStore.ReadFrom(machine.MbusFolder));
        var stored = Assert.Single(Directory.GetFiles(machine.MbusFolder, "agent-env*"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored));
        }
        else
        {
            Assert.DoesNotContain(token, File.ReadAllText(stored)); // DPAPI-protected
        }
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", install.Output);
        Assert.DoesNotContain(token, install.Output + install.Error);

        // Installed again from a shell without them (after an update, say), the service keeps them.
        machine.Env.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        machine.Env.Remove("OPENAI_API_KEY");
        var reinstalled = await machine.RunAsync("runner", "install");
        Assert.True(reinstalled.ExitCode == 0, reinstalled.Error);
        if (OperatingSystem.IsMacOS()) Assert.Contains("key in a file only your account can read, not the Keychain, because it is the saved choice", reinstalled.Output);
        Assert.Equal(2, AgentEnvironmentStore.ReadFrom(machine.MbusFolder).Count);

        Assert.Equal(["bootout", "bootstrap", "kickstart"], ran.Select(s => s.Arguments[0]).Distinct());
        Assert.Equal(["bootstrap", "gui/501", file], ran.First(s => s.Arguments[0] == "bootstrap").Arguments);
        Assert.Equal(["kickstart", "gui/501/" + RunnerServiceDefinitions.LaunchdLabel], ran[2].Arguments);

        ran.Clear();
        Assert.Equal(0, (await machine.RunAsync("runner", "uninstall")).ExitCode);
        Assert.Equal(["bootout", "gui/501/" + RunnerServiceDefinitions.LaunchdLabel], Assert.Single(ran).Arguments);
        Assert.False(File.Exists(file));
        Assert.Empty(AgentEnvironmentStore.ReadFrom(machine.MbusFolder));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Mac_install_waits_for_start_and_reports_a_timeout_with_the_start_command(bool starts, bool buildFolder)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        SignIn(machine);
        machine.Env["CLAUDE_CODE_OAUTH_TOKEN"] = "test-agent-sign-in";
        machine.MbusCommand = new(buildFolder ? "dotnet" : Path.Combine(machine.Home, "installed", "mbus"), ["mcp"]);
        machine.InstallAgent("mbus", "another mbus");
        using var running = new ServiceRunner(machine);
        var kicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<ServiceStep>();
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501, (step, _) =>
        {
            steps.Add(step);
            if (step.Arguments[0] == "kickstart") kicked.TrySetResult();
            return Task.FromResult((0, ""));
        });
        var install = machine.RunAsync("runner", "install");
        await kicked.Task;
        // Let the command reach its wait without advancing real time.
        await Task.Yield();
        machine.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(install.IsCompleted);
        if (starts) running.Run(new ServiceStep("/bin/launchctl", ["kickstart"]));
        var result = await WithTimePassing(machine, install);
        Assert.Equal(["bootout", "bootstrap", "kickstart"], steps.Select(s => s.Arguments[0]));
        Assert.Equal(["kickstart", "gui/501/ai.messagebus.runner"], steps[^1].Arguments);
        Assert.Contains("CLAUDE_CODE_OAUTH_TOKEN", result.Output);
        Assert.DoesNotContain("test-agent-sign-in", result.Output + result.Error);
        var installNote = buildFolder ? "This mbus runs from a build folder" : "The mbus on PATH is another one";
        Assert.Contains(installNote, result.Error);
        if (starts)
        {
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Contains("The runner has started", result.Output);
        }
        else
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("hasn't started within 30 seconds", result.Error);
            Assert.Contains("To start it: launchctl kickstart gui/501/ai.messagebus.runner", result.Error);
            Assert.True(result.Error.IndexOf(installNote, StringComparison.Ordinal) < result.Error.IndexOf("hasn't started", StringComparison.Ordinal));
            Assert.DoesNotContain("The runner has started", result.Output);
            Assert.DoesNotContain("The runner now starts", result.Output);
        }
    }

    [Fact]
    public async Task A_start_up_task_that_still_passes_the_removed_slots_option_starts_the_runner_which_ignores_it()
    {
        // the original issue: Tasks at once comes from the dashboard only. A task installed before that keeps passing --slots, and an
        // automatic update mustn't leave the machine with a runner that refuses to start.
        using var machine = new TestMachine();
        var log = Path.Combine(machine.MbusFolder, "logs", "runner.log");

        await machine.RunAsync("runner", "--slots", "1", "--log-file", log);

        // It got past the command line (here it stops for want of a sign-in) and said, once, that --slots does nothing.
        var written = File.ReadAllText(log);
        Assert.Contains(MessageBus.Cli.Runner.RunnerCliCommand.SlotsIgnored, written);
        Assert.Contains("mbus login", written);
    }

    [Fact]
    public async Task A_second_runner_finds_the_first_one_running_and_exits_0_without_starting()
    {
        // the original issue: exit 0 also ends the restart loop that started the duplicate.
        using var machine = new TestMachine();
        using var first = HoldLock(machine, Runner);
        var log = Path.Combine(machine.MbusFolder, "logs", "runner.log");

        // Not signed in either: had it started, it would stop for want of a sign-in, not exit 0.
        Assert.Equal(0, (await WithTimePassing(machine, machine.RunAsync("runner", "--log-file", log))).ExitCode);
        Assert.Contains($"A runner is already running here (pid {Runner.Pid})", File.ReadAllText(log));
    }

    [Fact]
    public async Task A_stop_request_stops_a_running_runner_and_keeps_one_a_service_starts_again_from_starting()
    {
        using var machine = new TestMachine();
        using (var instance = RunnerInstance.TryAcquire(machine.MbusFolder, out _))
        using (var running = instance!.WatchStop(machine.Time, TestContext.Current.CancellationToken))
        {
            machine.Time.Advance(RunnerInstance.StopCheckInterval);
            Assert.False(running.Token.IsCancellationRequested);
            RunnerInstance.RequestStop(machine.MbusFolder);
            machine.Time.Advance(RunnerInstance.StopCheckInterval);
            Assert.True(running.Token.IsCancellationRequested); // stops as on Ctrl+C: tasks go back, exit 0
        }

        // Started again by its service manager (with --log-file): exit 0 at once, and the request stays.
        var log = Path.Combine(machine.MbusFolder, "logs", "runner.log");
        Assert.Equal(0, (await machine.RunAsync("runner", "--log-file", log)).ExitCode);
        Assert.Contains("Not starting", File.ReadAllText(log));
        Assert.True(RunnerInstance.StopRequested(machine.MbusFolder));

        // Started by hand: you mean it to run, so the leftover request goes (here it then stops for want of a sign-in).
        machine.Env[MessageBus.Cli.Runner.RunnerRestart.SupervisedEnvVar] = "0";
        Assert.NotEqual(0, (await machine.RunAsync("runner")).ExitCode);
        Assert.False(RunnerInstance.StopRequested(machine.MbusFolder));
    }

    [Fact]
    public async Task Install_ends_a_runner_that_doesnt_stop_and_when_it_still_runs_fails_naming_it_and_registers_nothing()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        SignIn(machine);
        var ended = new List<int>();
        var ran = new List<ServiceStep>();
        var table = ProcessTable.Of([Record(Runner, "runner --log-file x")]);
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, @"CONTOSO\dev", 0, (step, _) =>
        {
            ran.Add(step);
            return Task.FromResult(step.Arguments[0] == "/Query" ? (1, "") : (0, ""));
        }, Opener(table, ended), table);
        using var stuck = HoldLock(machine, Runner); // never lets go

        var result = await WithTimePassing(machine, machine.RunAsync("runner", "install"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal([Runner.Pid], ended);
        Assert.Contains($"still running (process {Runner.Pid})", result.Error);
        // The stop request stays, so a restart loop mbus couldn't end keeps it stopped, and nothing is registered.
        Assert.True(RunnerInstance.StopRequested(machine.MbusFolder));
        Assert.DoesNotContain(ran, s => s.Arguments.Contains("/Create"));
    }

    [Fact]
    public async Task Stop_fails_naming_an_older_runner_of_this_folder_and_leaves_other_folders_and_users_runners_alone()
    {
        // A runner from before the original issue ignores the stop request and holds no lock: a free lock doesn't prove it stopped. But
        // only one of this settings folder's counts: its --log-file is in it and it runs as you.
        using var machine = new TestMachine();
        var mbus = Path.GetFullPath(machine.MbusCommand.Command);
        var here = $"\"{Path.Combine(machine.MbusFolder, "logs", "runner.log")}\"";
        var self = new ProcessRecord(Environment.ProcessId, "mbus", 1, $"{mbus} runner stop", mbus, 900, Owner: "501");
        ProcessRecord Runner(int pid, string arguments, string owner = "501") => new(pid, "mbus", 1, $"{mbus} {arguments}", mbus, 77, owner);
        List<ProcessRecord> processes =
        [
            self,
            Runner(5152, "runner env"), // not a runner
            Runner(5153, "--server https://x.example runner --server https://x.example stop"), // not a runner either
            Runner(5154, $"runner --log-file \"{Path.Combine(machine.Home, "other-home", "logs", "runner.log")}\""), // another MBUS_HOME
            Runner(5155, $"runner --log-file {here}", owner: "502"), // another user's
            Runner(5156, "runner --work-dir /tmp/runs"), // started by hand: can't be traced to a folder
        ];
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501, (_, _) => Task.FromResult((0, "")), processes: ProcessTable.Of(processes));

        var free = await machine.RunAsync("runner", "stop");
        Assert.True(free.ExitCode == 0, free.Error);
        Assert.Contains("process 5156", free.Error); // named, not blocking
        Assert.DoesNotContain("515" + "2", free.Error + free.Output);
        Assert.All(new[] { "5153", "5154", "5155" }, pid => Assert.DoesNotContain(pid, free.Error));

        processes.Add(Runner(5151, $"runner --log-file {here}"));
        var blocked = await machine.RunAsync("runner", "stop");
        Assert.Equal(1, blocked.ExitCode);
        Assert.Contains("An older runner may still be running: process 5151", blocked.Error);
    }

    [Fact]
    public async Task Stop_refuses_when_run_from_a_task_the_runner_started()
    {
        using var machine = new TestMachine();
        var mbus = Path.GetFullPath(machine.MbusCommand.Command);
        var runner = new ProcessRecord(6001, "mbus", 1, $"{mbus} runner --log-file x", mbus, 10);
        var agent = new ProcessRecord(6002, "claude", 6001, "claude -p", "/usr/local/bin/claude", 20);
        var self = new ProcessRecord(Environment.ProcessId, "mbus", 6002, $"{mbus} runner stop", mbus, 30);
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501, (_, _) => Task.FromResult((0, "")),
            processes: ProcessTable.Of([runner, agent, self]));

        var result = await machine.RunAsync("runner", "stop");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not from a task the runner started", result.Error);
        Assert.False(RunnerInstance.StopRequested(machine.MbusFolder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_runner_that_isnt_the_process_written_down_any_more_is_not_ended_and_is_named(bool changesAfterTheCheck)
    {
        // Its id went to another process: before the first check, or between it and the end (read again through the
        // held process that would be ended).
        using var machine = new TestMachine();
        var ended = new List<int>();
        var reused = Record(Runner, "runner --log-file x") with { StartTicks = Runner.StartTicks + 1 };
        var table = ProcessTable.Of([changesAfterTheCheck ? Record(Runner, "runner --log-file x") : reused]);
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.MacOs, "dev", 501, (_, _) => Task.FromResult((0, "")),
            pid => new HeldProcess(() => reused, () => { ended.Add(pid); return true; }), table);
        using var stuck = HoldLock(machine, Runner);

        var result = await WithTimePassing(machine, machine.RunAsync("runner", "stop"));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(ended);
        Assert.Contains($"mbus didn't end process {Runner.Pid}", result.Error);
    }

    [Fact]
    public async Task On_Windows_stop_fails_when_the_registered_task_cant_be_turned_off_and_skips_it_when_none_is_registered()
    {
        // Left on, the task starts the runner every minute and at the next sign-in.
        using var machine = new TestMachine();
        var ran = new List<ServiceStep>();
        var registered = true;
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, @"CONTOSO\dev", 0, (step, _) =>
        {
            ran.Add(step);
            return Task.FromResult(step.Arguments[0] switch
            {
                "/Query" => registered ? (0, TaskXml(machine, machine.MbusFolder)) : (1, ""),
                "/Change" => (1, "ERROR: Access is denied."),
                _ => (0, ""),
            });
        });

        var denied = await machine.RunAsync("runner", "stop");
        Assert.Equal(1, denied.ExitCode);
        Assert.Contains("turning off the mbus-runner task failed (exit 1: ERROR: Access is denied.)", denied.Error);
        Assert.Equal(["/Query", "/End", "/Change"], ran.Select(s => s.Arguments[0]));

        ran.Clear();
        registered = false;
        var none = await machine.RunAsync("runner", "stop");
        Assert.True(none.ExitCode == 0, none.Error);
        Assert.DoesNotContain(ran, s => s.Arguments[0] == "/Change");
    }

    [Fact]
    public async Task Stop_uninstall_and_install_leave_a_runner_set_up_with_other_settings_alone()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        // The task has one name per user: set up from settings folder A, it isn't B's to stop, turn off or remove when a
        // terminal has MBUS_HOME=B (no runner, nothing installed there).
        using var machine = new TestMachine();
        SignIn(machine);
        var other = Path.Combine(machine.Home, "other-home");
        var ran = new List<ServiceStep>();
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, @"CONTOSO\dev", 0, (step, _) =>
        {
            ran.Add(step);
            return Task.FromResult(step.Arguments[0] == "/Query" ? (0, TaskXml(machine, other)) : (0, ""));
        });

        var stop = await machine.RunAsync("runner", "stop");
        Assert.True(stop.ExitCode == 0, stop.Error);
        Assert.Contains("No runner is running here.", stop.Output);
        Assert.Contains($"uses the settings in {other}, so mbus left it alone", stop.Output);

        var uninstall = await machine.RunAsync("runner", "uninstall");
        Assert.True(uninstall.ExitCode == 0, uninstall.Error);
        Assert.Contains($"uses the settings in {other}", uninstall.Output);

        var install = await machine.RunAsync("runner", "install");
        Assert.Equal(1, install.ExitCode);
        Assert.Contains($"uses the settings in {other}", install.Error);

        // Only ever read: never ended, turned off, removed or replaced.
        Assert.All(ran, s => Assert.Equal("/Query", s.Arguments[0]));
    }

    [Fact]
    public async Task Stop_that_can_end_only_part_of_the_runners_processes_checks_again_and_still_turns_the_task_off()
    {
        // Process.Kill(entireProcessTree) throws an AggregateException when part of the tree can't be ended.
        using var machine = new TestMachine();
        var ran = new List<ServiceStep>();
        var table = ProcessTable.Of([Record(Runner, "runner --log-file x")]);
        machine.RunnerServices = () => new ServiceManager(ServicePlatform.Windows, @"CONTOSO\dev", 0, (step, _) =>
            {
                ran.Add(step);
                return Task.FromResult(step.Arguments[0] == "/Query" ? (0, TaskXml(machine, machine.MbusFolder)) : (0, ""));
            },
            pid => new HeldProcess(() => table.Get(pid), () => throw new AggregateException(new System.ComponentModel.Win32Exception(5, "Access is denied."))), table);
        using var stuck = HoldLock(machine, Runner); // the part that couldn't be ended still holds the lock

        var result = await WithTimePassing(machine, machine.RunAsync("runner", "stop"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"mbus couldn't end process {Runner.Pid} or some of the processes it started: Access is denied.", result.Error);
        Assert.Contains($"still running (process {Runner.Pid})", result.Error); // the lock was checked again
        Assert.Equal(["/Query", "/End", "/Change"], ran.Select(s => s.Arguments[0]));
    }

    /// <summary>The task <c>mbus runner install</c> registers for settings folder <paramref name="folder"/>, as <c>schtasks /Query /XML</c> prints it.</summary>
    private static string TaskXml(TestMachine machine, string folder) =>
        RunnerServiceDefinitions.Plan(new ServiceManager(ServicePlatform.Windows, @"CONTOSO\dev", 0, (_, _) => Task.FromResult((0, ""))),
            [machine.MbusCommand.Command], [], machine.Home, folder, new Dictionary<string, string>()).Definition;

    [Fact]
    public void The_restart_loop_is_the_logon_tasks_cmd_past_this_mbus_never_a_cmd_you_typed_in()
    {
        const string stable = @"C:\Users\dev\.local\bin\mbus.exe", versioned = @"C:\Users\dev\.local\bin\.mbus-versions\0.3.0\mbus.exe";
        var loopLine = $@"C:\Windows\System32\cmd.exe /d /s /c ""for /l %i in () do (""{stable}"" runner --log-file ""C:\Users\dev\.mbus\logs\runner.log"" && exit 0 & ping)""";
        var conhost = new ProcessRecord(10, "conhost", 4, @"conhost.exe --headless cmd.exe", @"C:\Windows\System32\conhost.exe", 1);
        var loop = new ProcessRecord(11, "cmd", 10, loopLine, @"C:\Windows\System32\cmd.exe", 2);
        var launcher = new ProcessRecord(12, "mbus", 11, $@"""{stable}"" runner --log-file x", stable, 3);
        var runner = new ProcessRecord(13, "mbus", 12, $@"""{versioned}"" runner --log-file x", versioned, 4);
        var table = ProcessTable.Of([conhost, loop, launcher, runner]);

        Assert.Equal(loop, RunnerProcesses.RestartLoop(13, table, [stable], supervised: true));
        Assert.Null(RunnerProcesses.RestartLoop(13, table, [stable], supervised: false));

        // Started by hand from a cmd window: that cmd isn't mbus's loop, so it is never recorded or ended.
        var typed = ProcessTable.Of([
            new ProcessRecord(20, "explorer", 1, "explorer.exe", @"C:\Windows\explorer.exe", 1),
            new ProcessRecord(21, "cmd", 20, @"""C:\Windows\System32\cmd.exe""", @"C:\Windows\System32\cmd.exe", 2),
            launcher with { ParentPid = 21 }, runner]);
        Assert.Null(RunnerProcesses.RestartLoop(13, typed, [stable], supervised: true));
    }

    [Fact]
    public void A_recorded_process_is_ended_only_while_it_is_the_same_one_and_never_when_it_is_the_caller_or_its_parent()
    {
        var runner = new ProcessRecord(30, "mbus", 1, "mbus runner", "/opt/mbus/.mbus-versions/0.3.0/mbus", 500);
        var shell = new ProcessRecord(40, "zsh", 30, "zsh", "/bin/zsh", 600);
        var caller = new ProcessRecord(41, "mbus", 40, "mbus runner stop", "/opt/mbus/mbus", 700);
        var table = ProcessTable.Of([runner, shell, caller]);
        var callers = RunnerProcesses.Protected(41, table);

        Assert.Equal(RunnerProcesses.Match.Same, RunnerProcesses.Check(runner.Identity!, ProcessTable.Of([runner]), new HashSet<int> { 99 }));
        // Its id now belongs to a process started later: refused.
        Assert.Equal(RunnerProcesses.Match.Changed, RunnerProcesses.Check(runner.Identity! with { StartTicks = 499 }, table, new HashSet<int> { 99 }));
        // The caller's own parents (here the runner it runs under) are never targets.
        Assert.Equal(RunnerProcesses.Match.Ancestor, RunnerProcesses.Check(runner.Identity!, table, callers));
        Assert.Equal(RunnerProcesses.Match.Ancestor, RunnerProcesses.Check(shell.Identity!, table, callers));
    }

    /// <summary>The runner a test pretends holds the lock (another process, so the test process is never a target).</summary>
    private static readonly ProcessIdentity Runner = new(4242, 638_000_000_000_000_000, Path.GetFullPath(Path.Combine("opt", "mbus", ".mbus-versions", "0.3.0", "mbus")));

    /// <summary>Holds a process from <paramref name="table"/>; ending it records its id.</summary>
    private static Func<int, HeldProcess?> Opener(ProcessTable table, List<int> ended) =>
        pid => table.Get(pid) is null ? null : new HeldProcess(() => table.Get(pid), () => { ended.Add(pid); return true; });

    private static ProcessRecord Record(ProcessIdentity identity, string arguments) =>
        new(identity.Pid, Path.GetFileNameWithoutExtension(identity.ExecutablePath), 1, $"{identity.ExecutablePath} {arguments}", identity.ExecutablePath, identity.StartTicks);

    /// <summary>Holds the runner lock in this process, recorded as <paramref name="runner"/>.</summary>
    private static RunnerInstance HoldLock(TestMachine machine, ProcessIdentity runner)
    {
        var instance = RunnerInstance.TryAcquire(machine.MbusFolder, out _) ?? throw new InvalidOperationException("The lock is taken.");
        Assert.True(RunnerInstance.TryWritePidFile(machine.MbusFolder, new RunnerHolder(runner, null)));
        return instance;
    }

    /// <summary>A fake launch agent that owns a real instance lock only between kickstart and bootout.</summary>
    private sealed class ServiceRunner(TestMachine machine) : IDisposable
    {
        private RunnerInstance? _instance;

        public (int, string) Run(ServiceStep step)
        {
            if (step.Arguments[0] == "bootout") Dispose();
            if (step.Arguments[0] == "kickstart") _instance = HoldLock(machine, Runner);
            return (step.Arguments[0] == "bootout" ? 3 : 0, "");
        }

        public void Dispose()
        {
            _instance?.Dispose();
            _instance = null;
        }
    }

    /// <summary>Lets fake time pass, a second at a time, until <paramref name="run"/> finishes (its waits are on the fake clock).</summary>
    private static async Task<T> WithTimePassing<T>(TestMachine machine, Task<T> run)
    {
        for (var i = 0; i < 10_000 && !run.IsCompleted; i++)
        {
            machine.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        return await run;
    }

    [Fact]
    public void The_runner_log_starts_a_new_file_at_its_size_limit_and_keeps_only_the_last_few()
    {
        // the original issue: a runner that runs for months doesn't fill the disk with its log.
        var folder = Path.Combine(Path.GetTempPath(), "mbus-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(folder, "runner.log");
            using (var log = new RunnerLogFile(path, maxBytes: 100, keep: 2))
            {
                for (var i = 1; i <= 10; i++)
                {
                    log.WriteLine($"line {i:00} " + new string('x', 30)); // 40 bytes a line: two lines a file
                }
            }

            // Newest in runner.log, the two files before it kept, nothing older; no line split across files.
            Assert.Equal(["line 09", "line 10"], File.ReadAllLines(path).Select(l => l[..7]));
            Assert.Equal(["line 07", "line 08"], File.ReadAllLines(path + ".1").Select(l => l[..7]));
            Assert.Equal(["line 05", "line 06"], File.ReadAllLines(path + ".2").Select(l => l[..7]));
            Assert.Equal(3, Directory.GetFiles(folder).Length);

            // Started again, it carries on in the same file.
            using (var log = new RunnerLogFile(path, maxBytes: 100, keep: 2))
            {
                log.WriteLine("line 11");
            }
            Assert.Equal(["line 09", "line 10", "line 11"], File.ReadAllLines(path).Select(l => l[..7]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void SignIn(TestMachine machine)
    {
        machine.Env["MBUS_SERVER"] = "https://mbus.example.com";
        machine.Env["MBUS_API_KEY"] = "mbus_test_key";
    }

    /// <summary>A plist dict's values by key.</summary>
    private static Dictionary<string, XNode> Plist(XElement dict)
    {
        var items = dict.Elements().ToList();
        var values = new Dictionary<string, XNode>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < items.Count; i += 2) values[items[i].Value] = items[i + 1];
        return values;
    }
}
