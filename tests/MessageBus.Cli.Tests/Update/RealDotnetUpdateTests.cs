using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.IO.Compression;
using System.Text.Json.Nodes;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Update;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Releases;
using MessageBus.Cli.Runner.Service;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Tests.Runner;
using MessageBus.Contracts.Json;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

/// <summary>Manual platform probe for the original issue. Never uses the global tool install or a real mbus server.</summary>
public sealed class RealDotnetUpdateTests(ITestOutputHelper output)
{
    private string _probeHome = "";
    private string? _updateSource;
    private string? _cliHome;
    private string _mcpEndpoint = "http://127.0.0.1:1";

    public static bool Enabled => Environment.GetEnvironmentVariable("MBUS_REAL_DOTNET_TESTS") == "1"
        && !new[] { "CI", "GITHUB_ACTIONS", "TF_BUILD", "GITLAB_CI", "JENKINS_URL", "TEAMCITY_VERSION", "BUILDKITE", "CIRCLECI" }.Any(name =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_updates_and_rolls_back_using_package_versions_when_both_tools_report_the_same_dev_build(bool scm)
    {
        if (scm && !OperatingSystem.IsWindows()) return;
        using var machine = new TestMachine();
        _probeHome = machine.MbusFolder;
        _cliHome = Path.Combine(machine.Home, "dotnet-home");
        var feed = Directory.CreateDirectory(Path.Combine(machine.Home, "feed")).FullName;
        var config = Path.Combine(machine.Home, "NuGet.Config");
        File.WriteAllText(config, DotnetToolUpdater.NuGetConfig(feed));
        var packages = Path.Combine(machine.Home, "packages");
        var tools = Path.Combine(machine.Home, "tools");
        const string older = "1.0.0", newer = "1.2.0", assemblyVersion = "9.0.0+dev.012abcd";
        var project = Path.Combine(machine.Home, "mbus.csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <PackAsTool>true</PackAsTool>
                <IsPackable>true</IsPackable>
                <ToolCommandName>mbus</ToolCommandName>
                <PackageId>{DotnetToolInstall.PackageId}</PackageId>
                <InformationalVersion>{assemblyVersion}</InformationalVersion>
                <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(machine.Home, "Program.cs"), """
            using System;
            using System.Reflection;
            if (args is not ["--version"]) return 2;
            Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
            return 0;
            """);
        foreach (var version in new[] { older, newer })
            await Run("dotnet", ["pack", project, "-c", "Release", "-o", feed, "-p:PackageVersion=" + version,
                "-p:RestoreConfigFile=" + config], packages);
        await Run("dotnet", ["tool", "install", "--tool-path", tools, DotnetToolInstall.PackageId,
            "--version", older, "--configfile", config], packages);

        string Entry(string version) => Directory.GetFiles(Path.Combine(tools, ".store", "messagebus.ai.cli", version),
            "mbus.dll", SearchOption.AllDirectories).Single();
        var entry = Entry(older);
        var install = DotnetToolInstall.Find(entry)!;
        var shim = install.Shim!;
        await Run(shim, ["--version"], expectedOutput: assemblyVersion);
        var dotnet = CommandLocator.Find("dotnet", Environment.GetEnvironmentVariable, [])!;
        var settings = machine.MbusFolder;
        var record = RunnerUpdateRecord.In(settings);
        DotnetToolUpdater Updater(string path) => new(DotnetToolInstall.Find(path)!, settings, dotnet, TextWriter.Null,
            record, assemblyVersion, machine.Time, () => ProcessTable.Empty, source: feed, entryAssembly: path)
            { MinimumServiceVersion = scm ? older : null };
        var updater = Updater(entry);
        await using var runner = new RunnerHarness(extensions: e => e with { SessionUpdater = updater });
        var ct = TestContext.Current.CancellationToken;
        var response = await runner.Routes.HandleAsync("POST", RunnerRoutes.Update,
            MessageBusJson.Serialize(new UpdateRequest { Version = newer, Via = RunnerInstallKind.DotnetTool }), ct);
        Assert.Equal(202, response.StatusCode);
        var ready = await runner.Engine.UpdateReady.WaitAsync(ct);
        Assert.True(await updater.InstallAsync(ready, ct), updater.LastResult?.Reason);
        var root = Path.Combine(settings, "update");
        var attempt = DotnetToolUpdater.Read<ToolUpdateAttempt>(Path.Combine(root, "attempt.json"))!;
        Assert.Equal(older, attempt.From);
        Assert.Equal(newer, attempt.To);

        // Enter the real appliers without registering a service.
        Task<CommandResult?> RunOffline(string executable, IReadOnlyList<string> args, TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment) =>
            DotnetToolUpdater.RunCommand(executable, args.Count == 9 && args[6] == newer
                ? [.. args.Take(8), config] : args, timeout, environment);
        Task<int> StartApplier() => scm
            ? DotnetToolUpdater.RunServiceApplierAsync(root, tools, dotnet, () => ProcessTable.Empty, RunOffline)
            : DotnetToolUpdater.RunApplierAsync("start", root);
        if (scm) Assert.Equal(0, await StartApplier());
        else
        {
            File.Move(Path.Combine(root, OperatingSystem.IsWindows() ? "pending.cmd" : "pending.sh"), Path.Combine(root, "applying"));
            Assert.Equal(0, await DotnetToolUpdater.RunApplierAsync("apply", root));
        }
        var newEntry = Entry(newer);
        Updater(newEntry).Startup();
        Assert.Equal(RunnerUpdateOutcome.Updated, record.Load(assemblyVersion, DotnetToolInstall.Find(newEntry))?.Outcome);
        Assert.False(File.Exists(entry));
        await Run(shim, ["--version"], expectedOutput: assemblyVersion);

        // Simulate a runner that starts but cannot confirm its connection, with the original feed offline.
        Directory.Move(feed, feed + "-offline");
        for (var starts = scm ? 1 : 0; starts < 3; starts++)
            Assert.Equal(0, await StartApplier());
        Assert.Equal(RunnerUpdateOutcome.RolledBack,
            DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(root, "result.json"))?.Outcome);
        Assert.Equal(older, DotnetToolInstall.Find(Entry(older))?.Package?.Version);
        Assert.False(File.Exists(newEntry));
        await Run(shim, ["--version"], expectedOutput: assemblyVersion);
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = "Manual only: MBUS_REAL_DOTNET_TESTS=1 outside CI.")]
    public async Task Local_feed_update_with_and_without_running_mcp()
    {
        await using var endpoint = new ProbeEndpoint();
        _mcpEndpoint = endpoint.Url;
        var root = FindRoot();
        var temp = Path.Combine(Path.GetTempPath(), "mbus-real-dotnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        _probeHome = Path.Combine(temp, "settings");
        output.WriteLine("Probe folder (left for runner cleanup): " + temp);
        var feed = Path.Combine(temp, "feed");
        var tools = Path.Combine(temp, "tools & ' (probe)");
        var packages = Path.Combine(temp, "packages");
        Directory.CreateDirectory(feed);
        var config = Path.Combine(temp, "NuGet.Config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "local"), new XAttribute("value", feed))))).Save(config);
        const string older = "0.1.3-test.1", newer = "0.1.3-test.2";
        foreach (var version in new[] { older, newer })
            await Run("dotnet", ["pack", Path.Combine(root, "src/MessageBus.Cli.Tool"), "-c", "Release", "-o", feed,
                "-m:1", "-p:Version=" + version]);

        async Task<int> Tool(string verb, string version, bool downgrade = false, bool success = true, string? sourceConfig = null)
        {
            var args = new List<string> { "tool", verb, "MessageBus.Ai.Cli", "--tool-path", tools, "--version", version,
                "--configfile", sourceConfig ?? config, "--verbosity", "normal" };
            if (downgrade) args.Add("--allow-downgrade");
            return await Run("dotnet", args, packages, success);
        }
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        await Tool("install", older);
        await Run(shim, ["--version"], expectedOutput: older);
        output.WriteLine("Global package-cache nupkgs: " + string.Join(", ", Directory.Exists(packages)
            ? Directory.GetFiles(packages, "*.nupkg", SearchOption.AllDirectories) : []));
        output.WriteLine("Tool-store nupkgs: " + string.Join(", ", Directory.GetFiles(tools, "*.nupkg", SearchOption.AllDirectories)));
        // Keep the old package outside .store before dotnet removes that version.
        var recovery = Path.Combine(temp, "recovery");
        Directory.CreateDirectory(recovery);
        File.Copy(Directory.GetFiles(tools, "messagebus.ai.cli." + older + ".nupkg", SearchOption.AllDirectories).Single(),
            Path.Combine(recovery, "MessageBus.Ai.Cli." + older + ".nupkg"));
        var recoveryConfig = Path.Combine(temp, "Recovery.Config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
            new XElement("add", new XAttribute("key", "recovery"), new XAttribute("value", recovery))))).Save(recoveryConfig);
        await Tool("update", newer);
        await Run(shim, ["--version"], expectedOutput: newer);
        await Tool("update", older, downgrade: true);
        // Exercise the production CLI handoff, not an equivalent shell probe.
        _updateSource = feed;
        await Run(shim, ["update", "--yes"], packages);
        await Run(shim, ["--version"], packages, expectedOutput: newer);
        _updateSource = null;
        await Tool("update", older, downgrade: true);
        // A missing version gives a deterministic failure; verify that it preserves the installed tool.
        Assert.NotEqual(0, await Tool("update", "0.1.3-test.404", success: false));
        await Run(shim, ["--version"], expectedOutput: older);

        if (OperatingSystem.IsWindows())
        {
            // Hold ONLY the native image: it cannot load .store assemblies or spawn children
            // while its initial thread is suspended. End it without ever resuming it.
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcess(shim, new StringBuilder($"\"{shim}\" --version"), 0, 0, false,
                0x08000004 /* CREATE_NO_WINDOW | CREATE_SUSPENDED */, 0, null, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
            using var image = Process.GetProcessById(info.ProcessId);
            try
            {
                output.WriteLine("Suspended shim: " + JsonSerializer.Serialize(ProcessTable.ForThisMachine().Get(image.Id)));
                output.WriteLine("Update with shim image only exit: " + await Tool("update", newer, success: false));
                output.WriteLine("Suspended shim after update: " + JsonSerializer.Serialize(ProcessTable.ForThisMachine().Get(image.Id)));
                var oldStoreExists = Directory.Exists(Path.Combine(tools, ".store", "messagebus.ai.cli", older));
                output.WriteLine("Old store after shim-only update exists: " + oldStoreExists);
                // SDK 10.0.402 completes replacement, then returns 1 trying to delete the
                // moved, still mapped native image in its scratch folder. Exit alone is insufficient.
                await Run(shim, ["--version"], expectedOutput: oldStoreExists ? older : newer);
            }
            finally
            {
                if (!image.HasExited) image.Kill();
                await image.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
            await Tool("update", older, downgrade: true);
        }

        using var mcp = Start(shim, ["mcp"], packages);
        var errors = mcp.StandardError.ReadToEndAsync();
        try
        {
            await Request(mcp, 1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "update-probe", version = "1" } });
            await mcp.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await mcp.StandardInput.FlushAsync();
            await Request(mcp, 2, "tools/list", new { });
            var table = ProcessTable.ForThisMachine();
            output.WriteLine("ProcessTable: " + JsonSerializer.Serialize(table.Get(mcp.Id)));
            output.WriteLine("Install processes and MCP children: " + JsonSerializer.Serialize(table.All().Where(p =>
                p.ParentPid == mcp.Id || p.ExecutablePath?.StartsWith(tools, StringComparison.OrdinalIgnoreCase) == true)));
            if (OperatingSystem.IsMacOS()) await Run("/usr/sbin/lsof", ["-p", mcp.Id.ToString()], success: false);
            var exit = await Tool("update", newer, success: false);
            output.WriteLine("Update with MCP alive exit: " + exit);
            await Run(shim, ["--version"], expectedOutput: exit == 0 ? newer : older);
            output.WriteLine("Old store directory exists: " + Directory.Exists(Path.Combine(tools, ".store", "messagebus.ai.cli", older)));
            await Request(mcp, 3, "tools/list", new { });
            // First attachment call exercises networking after the update. This server has no
            // attachments, but the running MCP must still reply with a protocol result.
            await Request(mcp, 4, "tools/call", new { name = "fetch_attachment", arguments = new { threadId = "probe", attachmentId = "probe", path = Path.Combine(temp, "attachment") } });
            if (OperatingSystem.IsMacOS()) await Run("/usr/sbin/lsof", ["-p", mcp.Id.ToString()], success: false);
        }
        finally
        {
            if (!mcp.HasExited) mcp.Kill(entireProcessTree: true);
            await mcp.WaitForExitAsync();
            output.WriteLine("MCP stderr: " + await errors);
        }
        await Tool("update", newer);
        if (OperatingSystem.IsWindows())
        {
            // Use the wrapper's actual shell, with EOF on stdin, without registering a task.
            var command = $"dotnet tool update MessageBus.Ai.Cli --tool-path \"{tools}\" --version {older} --allow-downgrade --configfile \"{config}\" --verbosity normal";
            await Run("cmd.exe", [], packages, commandLine: "/d /s /c \"" + command + " < NUL\"");
            await Run(shim, ["--version"], expectedOutput: older);
            await WindowsBatchProbe(tools, command.Replace(older, newer).Replace(" --allow-downgrade", ""), command, packages);
            await Run(shim, ["--version"], expectedOutput: newer);
        }
        // Recovery must work even with the new version already present and the old one removed.
        Assert.False(Directory.Exists(Path.Combine(tools, ".store", "messagebus.ai.cli", older)));
        Directory.Move(feed, feed + "-unavailable");
        await Tool("update", older, downgrade: true, sourceConfig: recoveryConfig);
        await Run(shim, ["--version"], expectedOutput: older);
        Directory.Move(feed + "-unavailable", feed);
        await GeneratedApplier(temp, tools, feed, packages, older, newer);
        await GlobalProbe(temp, feed, config, packages, older, newer);
    }

    private async Task GeneratedApplier(string temp, string tools, string feed, string packages, string older, string newer)
    {
        var settings = Path.Combine(temp, "settings & ' (applier)");
        var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
        // A failed Windows update can leave another copy in .store/.stage. Copy only
        // the installed version, just as the running tool supplies its own entry assembly.
        var installed = Path.Combine(tools, ".store", "messagebus.ai.cli", older);
        var entry = Directory.GetFiles(installed, "mbus.dll", SearchOption.AllDirectories).Single();
        var dotnet = CommandLocator.Find("dotnet", Environment.GetEnvironmentVariable, [])!;
        var updater = new DotnetToolUpdater(DotnetToolInstall.Find(entry)!, settings, dotnet, TextWriter.Null,
            RunnerUpdateRecord.In(settings), older, TimeProvider.System, source: feed, entryAssembly: entry);
        using (var holder = Start(shim, ["mcp"], packages))
        {
            try
            {
                await Request(holder, 1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "applier-holder", version = "1" } });
                Assert.False(await updater.InstallAsync(new() { Version = newer }, TestContext.Current.CancellationToken));
                Assert.True(updater.WaitingAgain);
                Assert.False(File.Exists(Path.Combine(settings, "update", "pending.sh")));
                Assert.False(File.Exists(Path.Combine(settings, "update", "pending.cmd")));
                await Run(shim, ["--version"], expectedOutput: older);
            }
            finally { if (!holder.HasExited) holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(); }
        }
        Assert.True(await updater.InstallAsync(new() { Version = newer }, TestContext.Current.CancellationToken), updater.LastResult?.Reason);
        var wrapper = Path.Combine(settings, "update", OperatingSystem.IsWindows() ? "wrapper.cmd" : "wrapper.sh");
        async Task StartWrapper() => await Run(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            OperatingSystem.IsWindows() ? [] : [wrapper], packages,
            commandLine: OperatingSystem.IsWindows() ? "/d /s /c \"\"" + wrapper + "\"\"" : null);
        // A holder opening after preparation must be caught by the independent applier.
        using (var late = Start(shim, ["mcp"], packages))
        {
            try
            {
                await Request(late, 1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "late-holder", version = "1" } });
                await StartWrapper();
                await Run(shim, ["--version"], expectedOutput: older);
                Assert.True(DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(settings, "update", "result.json"))?.FileInUse);
                updater.Startup();
                Assert.Equal(newer, updater.Waiting?.Version);
            }
            finally { if (!late.HasExited) late.Kill(entireProcessTree: true); await late.WaitForExitAsync(); }
        }
        Assert.True(await updater.InstallAsync(new() { Version = newer }, TestContext.Current.CancellationToken), updater.LastResult?.Reason);
        await StartWrapper();
        await Run(shim, ["--version"], expectedOutput: newer);
        Assert.Equal(RunnerUpdateOutcome.Updated, DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(settings, "update", "result.json"))?.Outcome);
        // Three starts without a connection confirmation must restore the saved package.
        await StartWrapper();
        await StartWrapper();
        await Run(shim, ["--version"], expectedOutput: older);
        Assert.Equal(RunnerUpdateOutcome.RolledBack, DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(settings, "update", "result.json"))?.Outcome);

        // A completed rollback must never supervise a later manual reinstall.
        await ToolUpdate(newer);
        await StartWrapper();
        await Run(shim, ["--version"], expectedOutput: newer);
        await ToolUpdate(older);
        async Task ToolUpdate(string version) => await Run("dotnet", ["tool", "update", "--tool-path", tools,
            DotnetToolInstall.PackageId, "--version", version, "--allow-downgrade", "--configfile", Path.Combine(temp, "NuGet.Config")], packages);

        const string bad = "0.1.3-test.3";
        var original = Directory.GetFiles(feed, "*" + newer + ".nupkg").Single();
        var broken = Path.Combine(feed, "MessageBus.Ai.Cli." + bad + ".nupkg");
        File.Copy(original, broken);
        using (var archive = ZipFile.Open(broken, ZipArchiveMode.Update))
        {
            foreach (var item in archive.Entries.Where(e => e.FullName.EndsWith(".nuspec") || e.FullName.EndsWith(".runtimeconfig.json")).ToArray())
            {
                string text;
                using (var reader = new StreamReader(item.Open())) text = reader.ReadToEnd();
                var name = item.FullName;
                item.Delete();
                if (name.EndsWith(".nuspec")) text = text.Replace("<version>" + newer + "</version>", "<version>" + bad + "</version>");
                else
                {
                    var json = JsonNode.Parse(text)!;
                    var options = json["runtimeOptions"]!;
                    options["rollForward"] = "Disable";
                    if (options["framework"] is { } framework) framework["version"] = "99.0.0";
                    if (options["frameworks"] is JsonArray frameworks)
                        foreach (var f in frameworks) f!["version"] = "99.0.0";
                    text = json.ToJsonString();
                }
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        // The installed old package is still the source for the independent applier copy.
        Assert.True(await updater.InstallAsync(new() { Version = bad }, TestContext.Current.CancellationToken), updater.LastResult?.Reason);
        await StartWrapper();
        await Run(shim, ["--version"], expectedOutput: older);
        Assert.Equal(RunnerUpdateOutcome.RolledBack, DotnetToolUpdater.Read<ToolUpdateResult>(Path.Combine(settings, "update", "result.json"))?.Outcome);
        output.WriteLine("Generated applier passed: holder refusal, update, unconfirmed-start rollback and bad-package recovery.");
    }

    private async Task GlobalProbe(string temp, string feed, string config, string packages, string older, string newer)
    {
        _cliHome = Path.Combine(temp, "global-home");
        try
        {
            await Run("dotnet", ["tool", "install", "-g", DotnetToolInstall.PackageId, "--version", older, "--configfile", config], packages);
            var tools = Path.Combine(_cliHome, ".dotnet", "tools");
            var shim = Path.Combine(tools, OperatingSystem.IsWindows() ? "mbus.exe" : "mbus");
            var entry = Directory.GetFiles(Path.Combine(tools, ".store", "messagebus.ai.cli", older), "mbus.dll", SearchOption.AllDirectories).Single();
            var settings = Path.Combine(temp, "global-settings");
            var updater = new DotnetToolUpdater(DotnetToolInstall.Find(entry)! with { Global = true }, settings, CommandLocator.Find("dotnet", Environment.GetEnvironmentVariable, [])!,
                TextWriter.Null, RunnerUpdateRecord.In(settings), older, TimeProvider.System, source: feed, entryAssembly: entry);
            Assert.True(await updater.InstallAsync(new() { Version = newer }, TestContext.Current.CancellationToken), updater.LastResult?.Reason);
            var wrapper = Path.Combine(settings, "update", OperatingSystem.IsWindows() ? "wrapper.cmd" : "wrapper.sh");
            await Run(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", OperatingSystem.IsWindows() ? [] : [wrapper], packages,
                commandLine: OperatingSystem.IsWindows() ? "/d /s /c \"\"" + wrapper + "\"\"" : null);
            await Run(shim, ["--version"], packages, expectedOutput: newer);
        }
        finally { _cliHome = null; }
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = "Manual only: MBUS_REAL_DOTNET_TESTS=1 outside CI.")]
    public async Task Windows_generated_loop_runs_once_with_and_without_hook()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mbus-loop & (probe)-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _probeHome = Path.Combine(directory, "settings");
        Directory.CreateDirectory(Path.Combine(directory, "logs"));
        var fake = Path.Combine(directory, "fake.cmd");
        await File.WriteAllTextAsync(fake, "@echo runner-started\r\n@exit /b 0\r\n");
        var manager = new ServiceManager(ServicePlatform.Windows, "user", 0, (_, _) => throw new Exception("No service operations"));
        var plan = RunnerServiceDefinitions.Plan(manager, [fake], [], directory, directory, new Dictionary<string, string>());
        var args = XDocument.Parse(plan.Definition).Descendants().Single(e => e.Name.LocalName == "Arguments").Value;
        var loop = args[(args.IndexOf("for /l", StringComparison.Ordinal))..].TrimEnd('"');
        loop = loop.Replace("for /l %i in ()", "for %i in (1)", StringComparison.Ordinal);
        var hook = Path.Combine(directory, "update", "wrapper.cmd");
        var marker = Path.Combine(directory, "hook.txt");
        foreach (var present in new[] { false, true })
        {
            if (present)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
                await File.WriteAllTextAsync(hook, $"@echo hook>\"{marker}\"\r\n@exit /b 0\r\n");
            }
            File.Delete(Path.Combine(directory, "logs", "runner-errors.log"));
            await Run("cmd.exe", [], commandLine: "/d /s /c \"" + loop + "\"");
            Assert.Contains("runner-started", await File.ReadAllTextAsync(Path.Combine(directory, "logs", "runner-errors.log")));
            Assert.Equal(present, File.Exists(marker));
        }
    }

    private async Task WindowsBatchProbe(string directory, string command, string resetCommand, string packages)
    {
        var pending = Path.Combine(directory, "pending.cmd");
        var applying = Path.Combine(directory, "applying.cmd");
        var marker = Path.Combine(directory, "applied.txt");
        // Transfer control on the SAME parsed line as ren: cmd must never read another line
        // from the old filename. The renamed invocation takes a separate branch and exits.
        await File.WriteAllTextAsync(pending, $"""
            @echo off
            setlocal DisableDelayedExpansion
            if "%~1"=="apply" goto apply
            ren "%~f0" "applying.cmd" && "%~dp0applying.cmd" apply
            :apply
            {command}
            if errorlevel 1 exit /b 1
            > "%~dp0applied.txt" echo applied
            exit /b 0
            """.ReplaceLineEndings("\r\n"), TestContext.Current.CancellationToken);
        await Run("cmd.exe", [], packages, commandLine: $"/d /s /c \"\"{pending}\" < NUL\"");
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(applying));
        Assert.Equal("applied", (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Trim());

        // Preferred wrapper shape: a stable wrapper renames an applier that is not running.
        await Run("cmd.exe", [], packages, commandLine: "/d /s /c \"" + resetCommand + " < NUL\"");
        File.Move(applying, pending);
        File.Delete(marker);
        var wrapper = Path.Combine(directory, "wrapper.cmd");
        await File.WriteAllTextAsync(wrapper, """
            @echo off
            setlocal DisableDelayedExpansion
            if not exist "%~dp0pending.cmd" exit /b 0
            ren "%~dp0pending.cmd" "applying.cmd" || exit /b 1
            call "%~dp0applying.cmd" apply
            exit /b %errorlevel%
            """.ReplaceLineEndings("\r\n"), TestContext.Current.CancellationToken);
        await Run("cmd.exe", [], packages, commandLine: $"/d /s /c \"\"{wrapper}\" < NUL\"");
        Assert.False(File.Exists(pending));
        Assert.Equal("applied", (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)).Trim());
    }

    private async Task Request(Process process, int id, string method, object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            Assert.DoesNotContain("Could not load file or assembly", line);
            using var json = JsonDocument.Parse(line);
            output.WriteLine("MCP " + method + ": " + (method == "tools/list" && json.RootElement.TryGetProperty("result", out _)
                ? json.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() + " tools"
                : line));
            if (json.RootElement.TryGetProperty("id", out var responseId) && responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id) return;
        }
        Assert.Fail("MCP exited before replying to " + method);
    }

    private Process Start(string file, IEnumerable<string> args, string? packages = null, string? commandLine = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (commandLine is not null) start.Arguments = commandLine;
        // Do not inherit the invoking worker's relay, task metadata, credentials or real settings.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MBUS_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["MBUS_HOME"] = _probeHome;
        Directory.CreateDirectory(start.Environment["MBUS_HOME"]!);
        File.WriteAllText(Path.Combine(start.Environment["MBUS_HOME"]!, "config.json"),
            JsonSerializer.Serialize(new { workspaceId = "probe", keyStore = "file", server = _mcpEndpoint }));
        start.Environment["MBUS_SERVER"] = _mcpEndpoint;
        start.Environment["MBUS_API_KEY"] = "synthetic-probe-only";
        if (_updateSource is not null) start.Environment["MBUS_UPDATE_NUGET_SOURCE"] = _updateSource;
        if (_cliHome is not null)
        {
            start.Environment["DOTNET_CLI_HOME"] = _cliHome;
            start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0";
        }
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (packages is not null) start.Environment["NUGET_PACKAGES"] = packages;
        output.WriteLine(file + " " + (commandLine ?? string.Join(" ", start.ArgumentList)));
        return Process.Start(start)!;
    }

    // The packed MCP requires successful discovery before it can start. Use a loopback-only
    // endpoint so the subprocess still uses real HTTP without contacting a hosted service.
    private sealed class ProbeEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;

        internal ProbeEndpoint()
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _serve = ServeAsync(_stop.Token);
        }

        internal string Url { get; }

        private async Task ServeAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct);
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var request = await reader.ReadLineAsync(ct);
                    while (await reader.ReadLineAsync(ct) is { Length: > 0 }) { }
                    var discovery = request?.StartsWith("GET /api/server ", StringComparison.Ordinal) == true;
                    var body = discovery ? """{"product":"messagebus.ai","version":"0.1.0","capabilities":[]}""" : "{}";
                    var status = discovery ? "200 OK" : "404 Not Found";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _serve;
            _stop.Dispose();
        }
    }

    private async Task<int> Run(string file, IEnumerable<string> args, string? packages = null, bool success = true, string? expectedOutput = null, string? commandLine = null)
    {
        using var process = Start(file, args, packages, commandLine);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var text = await stdout;
        output.WriteLine(text);
        if (expectedOutput is not null) Assert.Equal(expectedOutput, text.Trim());
        var errorText = await stderr;
        output.WriteLine(errorText);
        Assert.DoesNotContain("The batch file cannot be found", text + errorText, StringComparison.OrdinalIgnoreCase);
        output.WriteLine("Exit: " + process.ExitCode);
        if (success) Assert.Equal(0, process.ExitCode);
        return process.ExitCode;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MessageBus.Client.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run from a built repository checkout.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment,
        string? directory, ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
