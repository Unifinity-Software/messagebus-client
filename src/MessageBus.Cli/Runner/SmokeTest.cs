using System.Globalization;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>One agent for <c>mbus doctor --smoke</c>: how the runner would start it, or why it wouldn't.</summary>
/// <param name="Kind">Which agent.</param>
/// <param name="Adapter">How the runner starts it; null when it isn't started.</param>
/// <param name="Executable">Its command; null when it isn't started.</param>
/// <param name="NotReady">Why the runner wouldn't start it here (not installed, not signed in, its sandbox not set up); null when it would.</param>
internal sealed record SmokeAgent(AgentKind Kind, IAgentAdapter? Adapter, string? Executable, string? NotReady = null);

/// <summary>One checked step: edit, build, test or commit.</summary>
/// <param name="Name">The step.</param>
/// <param name="Ok">Whether the check found it done.</param>
/// <param name="Detail">What was found: on success a short fact, on failure the first useful error line.</param>
internal sealed record SmokeStep(string Name, bool Ok, string Detail);

/// <summary>How one agent did.</summary>
internal sealed record SmokeResult(AgentKind Kind, string? NotReady, IReadOnlyList<SmokeStep> Steps, Usage? Usage, string? KeptFolder)
{
    /// <summary>Started, and every step checked out.</summary>
    public bool Passed => NotReady is null && Steps.Count > 0 && Steps.All(s => s.Ok);
}

/// <summary>Settings for one <see cref="SmokeTest"/>.</summary>
internal sealed record SmokeTestOptions
{
    /// <summary>The runner's work folder; each agent's run folder is made in it, as a real run's is.</summary>
    public required string WorkRoot { get; init; }

    /// <summary>git (a real one, not macOS's placeholder).</summary>
    public required string Git { get; init; }

    /// <summary><c>dotnet</c>, for a small .NET project; null for a shell-script project instead.</summary>
    public string? Dotnet { get; init; }

    /// <summary>The run isolation a real run gets here.</summary>
    public required IRunSandbox Sandbox { get; init; }

    /// <summary>The agent sign-ins stored for the runner service (the original issue), as a real run's agent gets them.</summary>
    public Func<IReadOnlyDictionary<string, string>> AgentEnvironment { get; init; } = () => new Dictionary<string, string>();

    /// <summary>The runner's own environment (a stored sign-in it sets itself is left to it).</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = _ => null;

    /// <summary>Keep each run folder afterwards, for a day (<see cref="RunWorkspace.HoldFor"/>).</summary>
    public bool Keep { get; init; }

    /// <summary>How long one agent may take before it is stopped.</summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Clock (for the time limit and the run folder's name).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Lines added to the prompt (tests give the fake agent its directions this way).</summary>
    public string? PromptSuffix { get; init; }
}

/// <summary>
/// <c>mbus doctor --smoke</c> (the original issue): runs one agent through a tiny developer task the way the runner would (a fresh run
/// folder under the work root, the same run isolation and the same command line, with a worker's permissions), then checks
/// the result itself rather than taking the agent's word: the file changed, the build and test left their output after
/// the edit, and the commit is on the branch with the change in it. No server is involved; the agent gets no mbus
/// tools and no machine key, and nothing is pushed.
/// </summary>
internal sealed class SmokeTest(SmokeTestOptions options)
{
    /// <summary>The scratch repository's branch.</summary>
    public const string Branch = "mbus-smoke";

    /// <summary>The message the agent commits with.</summary>
    public const string CommitMessage = "Smoke test: say after";

    /// <summary>The step names, in order.</summary>
    public static IReadOnlyList<string> StepNames { get; } = ["edit", "build", "test", "commit"];

    /// <summary>Runs <paramref name="agent"/> once (or reports why it isn't started).</summary>
    public async Task<SmokeResult> RunAsync(SmokeAgent agent, CancellationToken cancellationToken)
    {
        if (agent.NotReady is { } notReady || agent.Adapter is null || agent.Executable is null)
        {
            return new SmokeResult(agent.Kind, agent.NotReady ?? "it isn't installed.", [], null, null);
        }

        if (agent.Adapter is OpenCodeAdapter openCode)
        {
            var selected = await openCode.SelectAsync(null, cancellationToken).ConfigureAwait(false);
            if (selected.Error is { } error) return new(agent.Kind, error, [], null, null);
            agent = agent with { Adapter = selected.Adapter };
        }
        var now = options.Time.GetUtcNow();
        var id = $"smoke-{AgentKinds.Name(agent.Kind)}-{now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        var workspace = RunWorkspace.Create(options.WorkRoot, id, 1);
        workspace.AgentKind = agent.Adapter.Kind;
        workspace.LocalModelEndpoint = (agent.Adapter as OpenCodeAdapter)?.LocalEndpoint;
        var profile = PermissionProfiles.Defaults(PermissionProfiles.Worker);
        var project = options.Dotnet is { } dotnet ? ScratchProject.Dotnet(dotnet) : ScratchProject.Shell();
        var steps = new List<SmokeStep>();
        Usage? usage = null;
        try
        {
            var setupError = await CreateRepositoryAsync(workspace.WorkTree, project, cancellationToken).ConfigureAwait(false);
            if (setupError is not null)
            {
                steps.Add(new SmokeStep("edit", false, "Couldn't make the test repository: " + setupError));
                return Finish();
            }
            var baseCommit = (await GitAsync(workspace.WorkTree, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).Output;
            var delivery = Delivery(id, agent.Kind, profile, now);
            var prompt = Prompt(project) + (options.PromptSuffix is { } suffix ? "\n" + suffix : "");
            await File.WriteAllTextAsync(workspace.PromptFile, prompt, cancellationToken).ConfigureAwait(false);

            await options.Sandbox.PrepareAsync(workspace, delivery, profile, cancellationToken).ConfigureAwait(false);
            ProcessLaunch Sandboxed(ProcessLaunch launch) => options.Sandbox.Wrap(launch, workspace, profile);

            // An empty mbus settings folder of the run's own: the agent's mbus tools (and any mbus it runs) find no
            // sign-in and no machine key.
            var noMbus = Directory.CreateDirectory(Path.Combine(workspace.Root, "mbus-home")).FullName;
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["MBUS_RUN_DIR"] = workspace.Root,
                ["MBUS_PERMISSION_PROFILE"] = profile.Name,
                [Hosting.EnvVars.Home] = noMbus,
            };
            string? cannotStart = null;
            var launch = AgentRun.AgentProcessLaunch(agent.Adapter, options.Sandbox, agent.Executable, workspace, profile,
                new AgentLaunchContext(prompt, workspace.PromptFile, TestCommands.Apply(profile, workspace.WorkTree), workspace.WorkTree,
                    new Dictionary<string, string> { [Hosting.EnvVars.Home] = noMbus }),
                AgentRun.AgentEnvironment(environment, agent.Adapter, new SignInEnvironment(options.GetEnvironmentVariable, options.AgentEnvironment())),
                agentLaunch => cannotStart = agentLaunch.CannotStart);
            if (cannotStart is not null) return new(agent.Kind, cannotStart, [], null, workspace.Root);

            var parser = agent.Adapter.CreateParser();
            var gate = new object();
            var started = DateTime.UtcNow;
            int exit;
            IReadOnlyList<string> tail;
            using (var limit = new CancellationTokenSource(options.TimeLimit, options.Time))
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken))
            using (var process = AgentProcess.Start(launch, line => { lock (gate) { parser.Observe(line); } }, tailLines: 50,
                filterOutput: launch.OutputFilter))
            {
                options.Sandbox.Started(workspace, process.Id);
                using (stop.Token.Register(process.Kill))
                {
                    exit = await process.WaitForExitAsync().ConfigureAwait(false);
                }
                tail = process.Tail();
                cancellationToken.ThrowIfCancellationRequested();
                if (limit.IsCancellationRequested)
                {
                    steps.Add(new SmokeStep("edit", false, $"Stopped after {(int)options.TimeLimit.TotalMinutes} minutes."));
                }
            }
            AgentOutcome outcome;
            lock (gate)
            {
                outcome = parser.Complete();
            }
            usage = outcome.Usage;

            if (steps.Count == 0)
            {
                if (agent.Kind == AgentKind.Codex)
                {
                    var diagnostic = (exit != 0 || outcome.ReportedError ? CodexAuthentication.Refusal(outcome, tail) : null)
                        ?? AgentRun.Readable(outcome.ErrorText) ?? LastLine(tail);
                    var verified = exit == 0 && !outcome.ReportedError && outcome.Usage is { TokensOut: > 0 };
                    steps.Add(new SmokeStep("model", verified, verified ? "Codex completed an authenticated model request."
                        : diagnostic ?? "Codex didn't report a successful model response."));
                    if (!verified) return Finish();
                }
                // The agent's own words only explain a failure; they never decide a step.
                var said = AgentRun.Readable(outcome.ErrorText) ?? AgentRun.Readable(outcome.FinalText) ?? (exit != 0 ? LastLine(tail) : null);
                steps.AddRange(await CheckAsync(workspace, project, baseCommit, started, Sandboxed, cancellationToken).ConfigureAwait(false));
                if (said is not null && steps.FirstOrDefault(s => !s.Ok) is { } first)
                {
                    steps[steps.IndexOf(first)] = first with { Detail = $"{first.Detail} The agent said: {said}" };
                }
            }
            return Finish();
        }
        finally
        {
            try { await options.Sandbox.EndedAsync(workspace).ConfigureAwait(false); }
            catch
            {
                workspace.SandboxCleanupIncomplete = true;
                workspace.Hold(DateTimeOffset.MaxValue);
                workspace.Dispose();
                throw;
            }
            if (options.Keep)
            {
                // Like a real run's kept work (the original issue): the runner's housekeeping deletes it a day later.
                workspace.Hold(options.Time.GetUtcNow() + RunWorkspace.HoldFor);
                workspace.Dispose();
            }
            else
            {
                workspace.CleanUp(keepLog: false);
            }
        }

        SmokeResult Finish() => new(agent.Kind, null, steps, usage, options.Keep ? workspace.Root : null);
    }

    /// <summary>The four checks, from what is on disk and in git after the agent ended.</summary>
    internal async Task<IReadOnlyList<SmokeStep>> CheckAsync(RunWorkspace workspace, ScratchProject project, string baseCommit, DateTime agentStarted,
        Func<ProcessLaunch, ProcessLaunch> sandboxed, CancellationToken cancellationToken)
    {
        var tree = workspace.WorkTree;
        var editFile = Path.Combine(tree, project.EditFile);
        var text = File.Exists(editFile) ? await File.ReadAllTextAsync(editFile, cancellationToken).ConfigureAwait(false) : "";
        var edited = text.Contains(project.After, StringComparison.Ordinal) && !text.Contains(project.Before, StringComparison.Ordinal);
        var steps = new List<SmokeStep>
        {
            edited
                ? new("edit", true, $"{project.EditFile} changed")
                : new("edit", false, File.Exists(editFile) ? $"{project.EditFile} doesn't have the line `{project.After}`." : $"{project.EditFile} is missing."),
        };

        // Built after the edit: the build's output is newer than the edited file.
        var editedAt = File.Exists(editFile) ? File.GetLastWriteTimeUtc(editFile) : agentStarted;
        var built = project.BuildOutputs(tree).Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        steps.Add(built >= agentStarted.AddSeconds(-2) && built >= editedAt.AddSeconds(-2)
            ? new("build", true, "built after the edit")
            : new("build", false, (built == DateTime.MinValue ? "No build output: the build didn't run or failed." : "The build output is older than the edit.")
                + await FirstErrorAsync(project.Build, tree, sandboxed, cancellationToken).ConfigureAwait(false)));

        var testFile = Path.Combine(tree, ScratchProject.TestResultFile);
        var testResult = File.Exists(testFile) && File.GetLastWriteTimeUtc(testFile) >= agentStarted.AddSeconds(-2)
            ? (await File.ReadAllTextAsync(testFile, cancellationToken).ConfigureAwait(false)).Trim() : null;
        steps.Add(testResult == "passed"
            ? new("test", true, "passed")
            : new("test", false, (testResult is null ? "The test didn't run." : $"The test failed ({testResult}).")
                + await FirstErrorAsync(project.Test, tree, sandboxed, cancellationToken).ConfigureAwait(false)));

        steps.Add(await CheckCommitAsync(tree, project, baseCommit, cancellationToken).ConfigureAwait(false));
        return steps;
    }

    private async Task<SmokeStep> CheckCommitAsync(string tree, ScratchProject project, string baseCommit, CancellationToken cancellationToken)
    {
        var head = await GitAsync(tree, ["rev-parse", "--verify", "--quiet", "refs/heads/" + Branch], cancellationToken).ConfigureAwait(false);
        if (head.Exit != 0)
        {
            return new("commit", false, $"The branch {Branch} is gone.");
        }
        if (head.Output == baseCommit)
        {
            return new("commit", false, $"No new commit on {Branch}.");
        }
        if ((await GitAsync(tree, ["merge-base", "--is-ancestor", baseCommit, head.Output], cancellationToken).ConfigureAwait(false)).Exit != 0)
        {
            return new("commit", false, $"The commit on {Branch} doesn't follow the starting commit.");
        }
        var log = (await GitAsync(tree, ["log", "-1", "--format=%s%n%an <%ae>", head.Output], cancellationToken).ConfigureAwait(false)).Output.Split('\n');
        var subject = log[0].Trim();
        var author = log.Length > 1 ? log[1].Trim() : "";
        if (subject != CommitMessage)
        {
            return new("commit", false, $"The last commit's message is \"{subject}\", not \"{CommitMessage}\".");
        }
        var committed = await GitAsync(tree, ["show", $"{head.Output}:{project.EditFile.Replace('\\', '/')}"], cancellationToken).ConfigureAwait(false);
        if (committed.Exit != 0 || !committed.Output.Contains(project.After, StringComparison.Ordinal))
        {
            return new("commit", false, $"The commit doesn't include the change to {project.EditFile}.");
        }
        var count = (await GitAsync(tree, ["rev-list", "--count", $"{baseCommit}..{head.Output}"], cancellationToken).ConfigureAwait(false)).Output;
        return new("commit", true, $"{head.Output[..Math.Min(7, head.Output.Length)]} by {(author.Length > 0 ? author : "unknown")}"
            + (count is "1" or "" ? "" : $" ({count} commits)"));
    }

    /// <summary>
    /// For a build or test that left no result: runs it once more, the way the run would, and gives back its first error
    /// line, so the report says why instead of only that it didn't work.
    /// </summary>
    private async Task<string> FirstErrorAsync(string command, string tree, Func<ProcessLaunch, ProcessLaunch> sandboxed, CancellationToken cancellationToken)
    {
        try
        {
            using var shell = AgentProcess.Start(sandboxed(AgentProcess.Shell(command, tree)), tailLines: 40);
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2), options.Time);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
            using (both.Token.Register(shell.Kill))
            {
                var exit = await shell.WaitForExitAsync().ConfigureAwait(false);
                if (exit == 0)
                {
                    return $" Run here, {command} works.";
                }
            }
            var lines = shell.Tail().Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var error = lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault();
            return error is null ? $" Run here, {command} fails." : $" Run here, {command} says: {AgentText.OneLine(error, AgentRun.ErrorLength)}";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $" {command} couldn't start: {ex.Message}";
        }
    }

    /// <summary>The scratch repository: the project's files, committed on <see cref="Branch"/>. Null when ready, else why not.</summary>
    private async Task<string?> CreateRepositoryAsync(string tree, ScratchProject project, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(tree);
        foreach (var (name, content) in project.Files)
        {
            var path = Path.Combine(tree, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        }
        foreach (var args in new[]
        {
            new[] { "init", "--quiet", "--initial-branch=" + Branch },
            ["add", "--all"],
            ["-c", "user.name=mbus doctor", "-c", "user.email=doctor@messagebus.invalid", "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--no-verify", "-m", "Smoke test: start"],
        })
        {
            var result = await GitAsync(tree, args, cancellationToken).ConfigureAwait(false);
            if (result.Exit != 0)
            {
                return $"git {args.First(a => !a.StartsWith('-') && !a.Contains('='))} failed: {result.Output}";
            }
        }
        return null;
    }

    private async Task<(int Exit, string Output)> GitAsync(string tree, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        using var git = AgentProcess.Start(new ProcessLaunch
        {
            FileName = options.Git,
            Arguments = ["-C", tree, .. args],
            WorkingDirectory = tree,
            Environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" },
        }, line => { lock (lines) { lines.Add(line); } }, tailLines: 20);
        using var kill = cancellationToken.Register(git.Kill);
        var exit = await git.WaitForExitAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (lines)
        {
            return (exit, string.Join("\n", exit == 0 ? lines : git.Tail()).Trim());
        }
    }

    private static string? LastLine(IReadOnlyList<string> tail) =>
        tail.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith('{')) is { } line ? AgentText.OneLine(line, AgentRun.ErrorLength) : null;

    private static string Prompt(ScratchProject project) => $"""
        This is a smoke test from `mbus doctor --smoke`: a short check that you can edit a file, build, test and commit on
        this machine. Work only in this folder. You don't need the mbus tools, and nothing needs to be sent anywhere.

        1. In `{project.EditFile}`, change the line `{project.Before}` to `{project.After}`. Change nothing else.
        2. Build: run `{project.Build}`.
        3. Test: run `{project.Test}`. It prints "Test passed" when it passes.
        4. Commit the change on the current branch ({Branch}) with the message `{CommitMessage}`. Don't push.

        Leave the build output and test results where the build and test put them, and don't clean the folder: the check
        reads them after you finish. Then end with one line saying which steps worked. If a step fails, give its first error line and stop.
        """;

    private static TaskDelivery Delivery(string id, AgentKind kind, PermissionProfile profile, DateTimeOffset now) => new()
    {
        DeliveryId = id,
        Attempt = 1,
        LeaseExpiresAt = now.AddHours(1),
        AgentId = AgentKinds.Name(kind),
        Role = "developer",
        HeartbeatSeconds = 30,
        RunEnvironment = new RunEnvironment { Checkout = CheckoutMode.Empty, PermissionProfile = profile },
        Envelope = new MessageEnvelope
        {
            Id = "msg_" + id,
            Type = "task.implement",
            Source = "agent://local/doctor",
            Time = now,
            WorkspaceId = "local",
            Target = new RoleTarget { Role = "developer" },
            ThreadId = "thr_" + id,
            Title = "mbus doctor --smoke",
            Sender = new SenderIdentity { Kind = SenderKind.Agent },
            Body = new BodyRef { SizeBytes = 0, Sha256 = "" },
        },
    };
}

/// <summary>The tiny repository an agent works in: one line to change, a build and a test, nothing to download.</summary>
internal sealed record ScratchProject(IReadOnlyDictionary<string, string> Files, string EditFile, string Before, string After, string Build, string Test,
    Func<string, IEnumerable<string>> BuildOutputs)
{
    /// <summary>Where the test writes <c>passed</c> (or why it failed).</summary>
    public const string TestResultFile = "out/test.txt";

    /// <summary>A .NET console project with no packages (an empty package source list, so restore needs no network).</summary>
    public static ScratchProject Dotnet(string dotnet)
    {
        var framework = $"net{DotnetMajor(dotnet)}.0";
        return new(new Dictionary<string, string>
        {
            ["Smoke.csproj"] = $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>{framework}</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """,
            // Nothing from the folders above the run (the runner's own settings) changes the build.
            ["Directory.Build.props"] = "<Project />\n",
            ["Directory.Build.targets"] = "<Project />\n",
            ["Directory.Packages.props"] = "<Project />\n",
            ["NuGet.Config"] = """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                  </packageSources>
                </configuration>
                """,
            ["Program.cs"] = """
                const string Word = "before";

                if (args is ["test"])
                {
                    var passed = Word == "after";
                    Directory.CreateDirectory("out");
                    File.WriteAllText(Path.Combine("out", "test.txt"), passed ? "passed" : $"Word is {Word}, not after");
                    Console.WriteLine(passed ? "Test passed" : $"Test failed: Word is {Word}, not after");
                    return passed ? 0 : 1;
                }
                Console.WriteLine(Word);
                return 0;
                """,
            [".gitignore"] = "bin/\nobj/\nout/\n",
        }, "Program.cs", "const string Word = \"before\";", "const string Word = \"after\";",
            "dotnet build", "dotnet run --no-build -- test",
            tree => [Path.Combine(tree, "bin", "Debug", framework, "Smoke.dll")]);
    }

    /// <summary>A shell-script project, for a machine without .NET.</summary>
    public static ScratchProject Shell()
    {
        var windows = OperatingSystem.IsWindows();
        var files = new Dictionary<string, string>
        {
            ["word.txt"] = "word=before\n",
            [".gitignore"] = "out/\n",
        };
        if (windows)
        {
            files["build.cmd"] = "@echo off\r\nif not exist out mkdir out\r\ncopy /y word.txt out\\build.txt >nul\r\n";
            files["test.cmd"] = "@echo off\r\nif not exist out mkdir out\r\nfindstr /b /c:\"word=after\" word.txt >nul\r\n"
                + "if errorlevel 1 (\r\n  echo failed> out\\test.txt\r\n  echo Test failed: word.txt does not say word=after\r\n  exit /b 1\r\n)\r\n"
                + "echo passed> out\\test.txt\r\necho Test passed\r\n";
        }
        else
        {
            files["build.sh"] = "#!/bin/sh\nset -e\nmkdir -p out\ncp word.txt out/build.txt\n";
            files["test.sh"] = "#!/bin/sh\nmkdir -p out\nif grep -qx 'word=after' word.txt; then\n  echo passed > out/test.txt\n  echo 'Test passed'\nelse\n"
                + "  echo failed > out/test.txt\n  echo 'Test failed: word.txt does not say word=after'\n  exit 1\nfi\n";
        }
        return new(files, "word.txt", "word=before", "word=after",
            windows ? @".\build.cmd" : "sh build.sh", windows ? @".\test.cmd" : "sh test.sh",
            tree => [Path.Combine(tree, "out", "build.txt")]);
    }

    /// <summary>The SDK's major version (<c>dotnet --version</c>), for the project's target framework; 8 when it can't tell.</summary>
    private static int DotnetMajor(string dotnet)
    {
        try
        {
            string? version = null;
            using var process = AgentProcess.Start(new ProcessLaunch
            {
                FileName = dotnet,
                Arguments = ["--version"],
                WorkingDirectory = Path.GetTempPath(),
            }, line => version ??= line.Trim(), tailLines: 5);
            return process.WaitForExitAsync().GetAwaiter().GetResult() == 0
                && version?.Split('.')[0] is { } major && int.TryParse(major, CultureInfo.InvariantCulture, out var number) ? number : 8;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 8;
        }
    }
}
