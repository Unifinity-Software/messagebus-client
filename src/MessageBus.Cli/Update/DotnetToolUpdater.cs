using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Service;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Update;

/// <summary>Dotnet's service handoff. Its private applier copy survives replacement of .store.</summary>
public sealed class DotnetToolUpdater(DotnetToolInstall install, string directory, string dotnet, TextWriter log,
    RunnerUpdateRecord record, string currentVersion, TimeProvider time, Func<ProcessTable>? processes = null, string? source = null, string? entryAssembly = null, bool manual = false) : ISessionRunnerUpdater
{
    public string CurrentVersion => currentVersion;
    internal const string PrivateCommand = "__runner-update";
    private string Root => Path.Combine(directory, "update");
    private ProcessTable Processes => processes?.Invoke() ?? ProcessTable.ForThisMachine();
    public RunnerUpdateReport? LastResult { get; private set; }
    public bool WaitingAgain { get; private set; }
    public IReadOnlyList<ToolInstallHolder> Holders(bool final = false) => final
        ? ToolInstallHolders.FinalCheck(install, Processes, Environment.ProcessId, new HashSet<int> { Environment.ProcessId })
        : ToolInstallHolders.WhileWaiting(install, Processes, Environment.ProcessId);

    public void KeepWaiting(UpdateRequest request) => Write(Path.Combine(Root, "waiting.json"), request);
    public UpdateRequest? Waiting => Read<UpdateRequest>(Path.Combine(Root, "waiting.json"));
    public void ClearWaiting() => File.Delete(Path.Combine(Root, "waiting.json"));

    public async Task<bool> InstallAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        WaitingAgain = false;
        LastResult = null;
        try
        {
            if (request.Version != RunnerUpdates.Latest && !ReleaseFeed.IsPinnableVersion(request.Version))
                throw new IOException("The update needs an exact, valid package version.");
            if (Holders(final: true).Count > 0)
            {
                WaitingAgain = true;
                KeepWaiting(request);
                return false;
            }
            if (request.Version == RunnerUpdates.Latest)
                request = request with { Version = await LatestAsync(source ?? Environment.GetEnvironmentVariable("MBUS_UPDATE_NUGET_SOURCE"), request.Channel, cancellationToken) };
            if (!ReleaseFeed.IsPinnableVersion(request.Version) || !ReleaseFeed.IsPinnableVersion(currentVersion))
                throw new IOException("The update needs an exact, valid package version.");
            var comparison = ReleaseVersion.Compare(currentVersion, request.Version);
            if (comparison == 0 || (!request.Pinned && comparison > 0))
                throw new IOException(comparison == 0 ? "This version is already installed." : "An unpinned update cannot install an older version.");
            ValidatePaths(OperatingSystem.IsWindows(), Root, install.ToolsPath ?? "", dotnet);
            if (install.ToolsPath is null || install.Shim is null) throw new IOException("Local manifest tools cannot update through the runner service.");
            Directory.CreateDirectory(Root);
            var recovery = Path.Combine(Root, "recovery");
            Directory.CreateDirectory(recovery);
            foreach (var old in Directory.EnumerateFiles(recovery, "*.nupkg")) File.Delete(old);
            var packageRoot = Path.Combine(install.ToolsPath, ".store", "messagebus.ai.cli", currentVersion.ToLowerInvariant());
            var package = Directory.GetFiles(packageRoot, "*.nupkg", SearchOption.AllDirectories)
                .FirstOrDefault(p => Path.GetFileName(p).Equals($"messagebus.ai.cli.{currentVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("The installed package is missing; the previous version cannot be kept for offline recovery.");
            File.Copy(package, Path.Combine(recovery, $"MessageBus.Ai.Cli.{currentVersion}.nupkg"), true);
            File.WriteAllText(Path.Combine(Root, "NuGet.Config"), NuGetConfig(source ?? Environment.GetEnvironmentVariable("MBUS_UPDATE_NUGET_SOURCE") ?? "https://api.nuget.org/v3/index.json"));
            File.WriteAllText(Path.Combine(Root, "Recovery.Config"), NuGetConfig(recovery));
            // Copy the framework-dependent tool and its dependencies, never run the applier from .store.
            var copy = Path.Combine(Root, "applier");
            Directory.CreateDirectory(copy);
            // Only this private update directory is cleared; never the installed tool.
            foreach (var old in Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories)) File.Delete(old);
            var entry = entryAssembly ?? Assembly.GetEntryAssembly()!.Location;
            var programDirectory = Path.GetDirectoryName(entry)!;
            foreach (var file in Directory.EnumerateFiles(programDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(programDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
            var assembly = Path.Combine(copy, Path.GetFileName(entry));
            var attempt = new ToolUpdateAttempt(Guid.NewGuid().ToString("N"), currentVersion, request.Version, request.Pinned,
                install.ToolsPath, install.Shim, dotnet, 0, false,
                Read<ToolUpdateAttempt>(Path.Combine(Root, "attempt.json")) is { } previous && previous.To == request.Version ? previous.Retries : 0);
            foreach (var old in Directory.EnumerateFiles(Root, "applying.*")) File.Delete(old);
            attempt = attempt with { Global = install.Global, Channel = request.Channel };
            Write(Path.Combine(Root, "attempt.json"), attempt);
            File.Delete(Path.Combine(Root, "result.json"));
            File.Delete(Path.Combine(Root, "last-failure.txt"));
            var windows = OperatingSystem.IsWindows();
            WriteText(Path.Combine(Root, windows ? "wrapper.cmd" : "wrapper.sh"), Script(windows, dotnet, assembly, Root, "start"));
            WriteText(Path.Combine(Root, windows ? "pending.cmd" : "pending.sh"), Script(windows, dotnet, assembly, Root, "apply"));
            if (Holders(final: true).Count > 0)
            {
                File.Delete(Path.Combine(Root, windows ? "pending.cmd" : "pending.sh"));
                Write(Path.Combine(Root, "attempt.json"), attempt with { RetryPending = true });
                WaitingAgain = true;
                KeepWaiting(request);
                return false;
            }
            ClearWaiting();
            if (!manual) await log.WriteLineAsync("The service will update this dotnet tool before starting the runner again.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LastResult = new() { Outcome = RunnerUpdateOutcome.NotInstalled, FromVersion = currentVersion, ToVersion = request.Version,
                Reason = Diagnostic(ex.Message), At = time.GetUtcNow() };
            record.Save(LastResult, log, install);
            await log.WriteLineAsync("Update not installed: " + LastResult.Reason);
            return false;
        }
    }

    /// <summary>Consumes only results from this attempt; an update race returns to the ordinary holder wait.</summary>
    public void Startup()
    {
        try { ReadStartup(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("Could not read the service update result: " + Diagnostic(ex.Message));
        }
    }

    private void ReadStartup()
    {
        var failure = Path.Combine(Root, "last-failure.txt");
        if (File.Exists(failure)) log.WriteLine("Update recovery needs attention: " + File.ReadAllText(failure));
        var attempt = Read<ToolUpdateAttempt>(Path.Combine(Root, "attempt.json"));
        var result = Read<ToolUpdateResult>(Path.Combine(Root, "result.json"));
        if (attempt is null || !Valid(attempt) || attempt.RetryPending || attempt.Terminal && result is null) return;
        if (result is null)
        {
            LastResult = new() { Outcome = RunnerUpdateOutcome.NotInstalled, FromVersion = attempt.From, ToVersion = attempt.To,
                Reason = "The service did not finish the update step. Run mbus runner install once on this machine so it can update itself.", At = time.GetUtcNow() };
            record.Save(LastResult, log, install);
            return;
        }
        if (result.Id != attempt.Id) return;
        if (!attempt.Terminal && result.Outcome != RunnerUpdateOutcome.Updated && result.FileInUse && currentVersion.Equals(attempt.From, StringComparison.OrdinalIgnoreCase)
            && (Holders().Count > 0 || attempt.Retries == 0))
        {
            if (attempt.RetryPending) return;
            Write(Path.Combine(Root, "attempt.json"), attempt with { Retries = attempt.Retries + (Holders().Count == 0 ? 1 : 0), RetryPending = true });
            KeepWaiting(new() { Version = attempt.To, Pinned = attempt.Pinned, Channel = attempt.Channel, Via = RunnerInstallKind.DotnetTool });
            record.Clear();
            return;
        }
        var outcome = result.Outcome;
        if (outcome == RunnerUpdateOutcome.Updated && !currentVersion.Equals(attempt.To, StringComparison.OrdinalIgnoreCase))
            outcome = RunnerUpdateOutcome.NotInstalled;
        LastResult = new() { Outcome = outcome, FromVersion = attempt.From, ToVersion = attempt.To, Reason = result.Reason is null ? null : Diagnostic(result.Reason), At = result.At };
        record.Save(LastResult, log, install);
        if ((outcome != RunnerUpdateOutcome.Updated || attempt.Terminal) && (attempt.RecoveryTries == 0 || attempt.Terminal))
        {
            Write(Path.Combine(Root, "attempt.json"), attempt with { Terminal = true });
            File.Move(Path.Combine(Root, "result.json"), Path.Combine(Root, "consumed-result.json"), true);
        }
    }

    public void ConfirmConnected()
    {
        try
        {
            var path = Path.Combine(Root, "attempt.json");
            if (Read<ToolUpdateAttempt>(path) is { Confirmed: false, Terminal: false } attempt && Valid(attempt)
                && currentVersion.Equals(attempt.To, StringComparison.OrdinalIgnoreCase)
                && Read<ToolUpdateResult>(Path.Combine(Root, "result.json")) is { Outcome: RunnerUpdateOutcome.Updated } result && result.Id == attempt.Id)
            {
                Write(path, attempt with { Confirmed = true, Terminal = true });
                File.Move(Path.Combine(Root, "result.json"), Path.Combine(Root, "consumed-result.json"), true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("Could not confirm the runner update: " + Diagnostic(ex.Message));
        }
    }

    internal static async Task<string> LatestAsync(string? source, string? channel, CancellationToken ct)
    {
        var versions = new List<string>();
        if (source is not null && Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source, "*.nupkg"))
            {
                var name = Path.GetFileName(file);
                const string prefix = "messagebus.ai.cli.";
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) versions.Add(name[prefix.Length..^6]);
            }
        }
        else
        {
            // Registration metadata includes listed=false; flat-container lists alone cannot exclude pulled releases.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var index = "https://api.nuget.org/v3/registration5-gz-semver2/messagebus.ai.cli/index.json";
            if (source is not null)
            {
                using var service = JsonDocument.Parse(await client.GetStringAsync(source, ct));
                var registration = service.RootElement.GetProperty("resources").EnumerateArray()
                    .FirstOrDefault(r => r.GetProperty("@type").GetString() == "RegistrationsBaseUrl/3.6.0");
                if (registration.ValueKind == JsonValueKind.Undefined)
                    throw new IOException("The update source does not expose SemVer 2 registration metadata.");
                index = registration.GetProperty("@id").GetString()!.TrimEnd('/') + "/messagebus.ai.cli/index.json";
            }
            using var document = JsonDocument.Parse(await client.GetStringAsync(index, ct));
            foreach (var page in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (page.TryGetProperty("items", out var items)) Add(items);
                else
                {
                    using var loaded = JsonDocument.Parse(await client.GetStringAsync(page.GetProperty("@id").GetString(), ct));
                    Add(loaded.RootElement.GetProperty("items"));
                }
            }
            void Add(JsonElement items)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var entry = item.GetProperty("catalogEntry");
                    if (entry.TryGetProperty("listed", out var listed) && listed.ValueKind == JsonValueKind.False) continue;
                    if (entry.GetProperty("version").GetString() is { } version) versions.Add(version);
                }
            }
        }
        return SelectLatest(versions, channel) ?? throw new IOException("No listed package version is available from this machine's update source.");
    }

    internal static string? SelectLatest(IEnumerable<string> versions, string? channel)
    {
        var valid = versions.Where(ReleaseFeed.IsPinnableVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = channel == "preview" ? valid : valid.Where(v => !v.Split('+')[0].Contains('-')).ToArray();
        if (candidates.Length == 0) candidates = valid;
        return candidates.OrderByDescending(v => v, Comparer<string>.Create((a, b) => ReleaseVersion.Compare(a, b) ?? 0)).FirstOrDefault();
    }

    internal static string NuGetConfig(string source) => new XDocument(new XElement("configuration", new XElement("packageSources",
        new XElement("clear"), new XElement("add", new XAttribute("key", "update"), new XAttribute("value", source))))).ToString();

    internal static void ValidatePaths(bool windows, params string[] paths)
    {
        if (paths.Any(p => p.IndexOfAny(windows ? ['%', '"', '!', '^', '\r', '\n'] : ['\r', '\n']) >= 0))
            throw new IOException("This install path contains characters the service update cannot safely use. Move the tool to a simpler path.");
    }
    internal static string Sh(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    internal static string Script(bool windows, string dotnet, string assembly, string root, string mode)
    {
        ValidatePaths(windows, dotnet, assembly, root);
        return windows ? $"@echo off\r\nsetlocal DisableDelayedExpansion\r\n\"{dotnet}\" \"{assembly}\" {PrivateCommand} {mode} \"{root}\" < NUL\r\nexit /b %errorlevel%\r\n"
            : $"#!/bin/sh\nexec {Sh(dotnet)} {Sh(assembly)} {PrivateCommand} {mode} {Sh(root)} </dev/null\n";
    }
    internal static string Diagnostic(string text)
    {
        text = Regex.Replace(text, @"https?://[^\s<>""']+", "[source]", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"(?i)(password|token|apikey|api_key|authorization)\s*[:=]\s*\S+", "$1=[redacted]");
        return text.Length > 500 ? text[^500..] : text.Trim();
    }
    internal static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }
    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value));
        File.Move(temp, path, true);
    }
    private static void WriteText(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, true);
    }
    private static bool Valid(ToolUpdateAttempt attempt) => !string.IsNullOrEmpty(attempt.Id)
        && !string.IsNullOrEmpty(attempt.Tools) && Path.IsPathFullyQualified(attempt.Tools)
        && !string.IsNullOrEmpty(attempt.Shim) && Path.IsPathFullyQualified(attempt.Shim)
        && !string.IsNullOrEmpty(attempt.Dotnet) && Path.IsPathFullyQualified(attempt.Dotnet)
        && ReleaseFeed.IsPinnableVersion(attempt.From) && ReleaseFeed.IsPinnableVersion(attempt.To)
        && (attempt.Pinned || ReleaseVersion.Compare(attempt.To, attempt.From) >= 0);

    /// <summary>Internal service entry point, running entirely outside the tools install.</summary>
    public static async Task<int> RunApplierAsync(string mode, string root)
    {
        try
        {
            var path = Path.Combine(root, "attempt.json");
            var attempt = Read<ToolUpdateAttempt>(path);
            if (attempt is null || !Valid(attempt)) return 1;
            ValidatePaths(OperatingSystem.IsWindows(), root, attempt.Tools, attempt.Shim, attempt.Dotnet);
            if (mode == "cli" || mode.StartsWith("cli-wait-", StringComparison.Ordinal))
            {
                if (mode != "cli")
                {
                    if (!int.TryParse(mode[9..], out var pid) || pid <= 0) return 1;
                    try { using var parent = System.Diagnostics.Process.GetProcessById(pid); await parent.WaitForExitAsync(); }
                    catch (ArgumentException) { } // It already exited before we opened it.
                }
                await Apply(root, attempt, printOutput: true);
                var result = Read<ToolUpdateResult>(Path.Combine(root, "result.json"));
                if (result?.Outcome == RunnerUpdateOutcome.Updated)
                {
                    Console.WriteLine($"mbus {attempt.To} is installed.");
                    return 0;
                }
                Console.Error.WriteLine("Update not installed: " + result?.Reason);
                return 1;
            }
            if (mode == "apply") { await Apply(root, attempt); return 0; }
            if (mode != "start") return 1;
            // OS file locking releases on a crash, unlike a stale directory lock.
            using var held = new FileStream(Path.Combine(attempt.Tools, ".mbus-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
            var pending = Path.Combine(root, "pending" + extension);
            var applying = Path.Combine(root, "applying." + attempt.Id + extension);
            if (attempt.Terminal || attempt.RetryPending) return 0;
            if (StartDecision(attempt, Read<ToolUpdateResult>(Path.Combine(root, "result.json")), File.Exists(pending), File.Exists(applying)) == ToolStartAction.Apply)
            {
                File.Move(pending, applying, true); // rename before executing, including on Windows
                var run = OperatingSystem.IsWindows()
                    ? await CommandRunner.RunAsync("cmd.exe", [], CancellationToken.None, TimeSpan.FromMinutes(6), commandLine: "/d /s /c \"\"" + applying + "\"\"")
                    : await CommandRunner.RunAsync("/bin/sh", [applying], CancellationToken.None, TimeSpan.FromMinutes(6));
                if (Read<ToolUpdateResult>(Path.Combine(root, "result.json"))?.Id != attempt.Id)
                {
                    if (string.Equals(await Version(attempt.Shim), attempt.To, StringComparison.OrdinalIgnoreCase))
                        Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.Updated, null, false));
                    else await Repair(root, attempt, "The update process did not finish; restoring the previous version.", false);
                }
            }
            else if (StartDecision(attempt, Read<ToolUpdateResult>(Path.Combine(root, "result.json")), false, File.Exists(applying)) == ToolStartAction.CheckInterrupted)
            {
                if (string.Equals(await Version(attempt.Shim), attempt.To, StringComparison.OrdinalIgnoreCase))
                    Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.Updated, null, false));
                else if (string.Equals(await Version(attempt.Shim), attempt.From, StringComparison.OrdinalIgnoreCase))
                {
                    Write(path, attempt with { Terminal = true });
                    Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, "The interrupted update was not installed.", false));
                }
                else await Repair(root, attempt, "The update was interrupted; restoring the previous version.", false);
            }
            attempt = Read<ToolUpdateAttempt>(path) ?? attempt;
            var startupResult = Read<ToolUpdateResult>(Path.Combine(root, "result.json"));
            if (StartDecision(attempt, startupResult, false, false) == ToolStartAction.CountStart)
            {
                attempt = attempt with { Starts = attempt.Starts + 1 };
                Write(path, attempt);
                if (attempt.Starts >= 3 && string.Equals(await Version(attempt.Shim), attempt.To, StringComparison.OrdinalIgnoreCase))
                    await Repair(root, attempt, "The updated runner did not confirm a connection after three starts.", true);
            }
            else if (StartDecision(attempt, startupResult, false, false) == ToolStartAction.Recover)
            {
                var running = await Version(attempt.Shim);
                switch (RecoveryDecision(attempt, running))
                {
                    case ToolRecoveryAction.Repair:
                        await Repair(root, attempt, "Retrying interrupted recovery.", false);
                        break;
                    default:
                        Write(path, attempt with { Terminal = true });
                        var previous = string.Equals(running, attempt.From, StringComparison.OrdinalIgnoreCase);
                        var outcome = previous && attempt.RecoveryInstalled ? RunnerUpdateOutcome.RolledBack
                            : RunnerUpdateOutcome.NotInstalled;
                        Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, outcome,
                            previous ? "Recovery completed before this start." : "A manually installed version was found; leaving it in place.", false));
                        break;
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            try { File.WriteAllText(Path.Combine(root, "last-failure.txt"), Diagnostic(ex.Message)); } catch (IOException) { }
            return 1;
        }
    }

    internal static ToolStartAction StartDecision(ToolUpdateAttempt attempt, ToolUpdateResult? result, bool pending, bool applying)
    {
        if (attempt.Terminal || attempt.Confirmed || attempt.RetryPending) return ToolStartAction.None;
        if (pending) return ToolStartAction.Apply;
        if (attempt.RecoveryTries > 0) return attempt.RecoveryTries < 3 ? ToolStartAction.Recover : ToolStartAction.None;
        if (result?.Id != attempt.Id) return applying ? ToolStartAction.CheckInterrupted : ToolStartAction.None;
        if (result.Outcome == RunnerUpdateOutcome.Updated) return ToolStartAction.CountStart;
        return ToolStartAction.None;
    }

    internal static ToolRecoveryAction RecoveryDecision(ToolUpdateAttempt attempt, string? running)
    {
        if (string.Equals(running, attempt.From, StringComparison.OrdinalIgnoreCase)) return ToolRecoveryAction.Done;
        if (running is null || (string.Equals(running, attempt.To, StringComparison.OrdinalIgnoreCase)
            && attempt.RecoveryInstalled && attempt.Starts >= 3)) return ToolRecoveryAction.Repair;
        return ToolRecoveryAction.Manual;
    }

    private static IReadOnlyList<ToolInstallHolder> ApplierHolders(ToolUpdateAttempt attempt) =>
        ToolInstallHolders.FinalCheck(new(attempt.Tools, attempt.Global), ProcessTable.ForThisMachine(), Environment.ProcessId,
            new HashSet<int> { Environment.ProcessId });

    private static async Task<string?> Version(string shim)
    {
        var result = await CommandRunner.RunAsync(shim, ["--version"], CancellationToken.None, TimeSpan.FromSeconds(30));
        return result?.ExitCode == 0 ? result.Output.Trim() : null;
    }
    private static async Task<CommandResult?> Update(string root, ToolUpdateAttempt attempt, bool recovery)
    {
        List<string> args = ["tool", "update", .. attempt.Global ? new[] { "-g" } : new[] { "--tool-path", attempt.Tools }, DotnetToolInstall.PackageId, "--version", recovery ? attempt.From : attempt.To,
            "--configfile", Path.Combine(root, recovery ? "Recovery.Config" : "NuGet.Config")];
        if (recovery || attempt.Pinned) args.Add("--allow-downgrade");
        return await CommandRunner.RunAsync(attempt.Dotnet, args, CancellationToken.None, TimeSpan.FromMinutes(2),
            new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" });
    }
    private static async Task Apply(string root, ToolUpdateAttempt attempt, bool printOutput = false)
    {
        using var held = new FileStream(Path.Combine(attempt.Tools, ".mbus-applier.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ApplierHolders(attempt).Count > 0)
        {
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, "Processes are using this install.", true));
            return;
        }
        var update = await Update(root, attempt, false);
        if (printOutput && update is not null) Console.WriteLine(update.AllOutput);
        var check = await CommandRunner.RunAsync(attempt.Shim, ["--version"], CancellationToken.None, TimeSpan.FromSeconds(30));
        var running = check?.ExitCode == 0 ? check.Output.Trim() : null;
        // SDKs can report failure after replacing the tool. The shim's output is authoritative.
        if (string.Equals(running, attempt.To, StringComparison.OrdinalIgnoreCase))
        {
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.Updated, null, false));
            return;
        }
        var reason = update?.AllOutput ?? "dotnet did not start or did not finish within two minutes.";
        if (running is null) reason += " New tool failed its version check: " + (check?.AllOutput ?? "The tool did not start or did not finish within 30 seconds.");
        var fileInUse = reason.Contains("denied", StringComparison.OrdinalIgnoreCase) || reason.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("in use", StringComparison.OrdinalIgnoreCase) || ApplierHolders(attempt).Count > 0;
        // A failed start check only counts as a rollback if the new version actually installed.
        var installed = Directory.Exists(Path.Combine(attempt.Tools, ".store", "messagebus.ai.cli", attempt.To.ToLowerInvariant()));
        if (string.Equals(running, attempt.From, StringComparison.OrdinalIgnoreCase))
        {
            Write(Path.Combine(root, "attempt.json"), attempt with { Terminal = !fileInUse });
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, Diagnostic(reason), fileInUse));
        }
        else await Repair(root, attempt, reason, installed, fileInUse, alreadyLocked: true);
    }
    private static async Task Repair(string root, ToolUpdateAttempt attempt, string reason, bool installed, bool fileInUse = false, bool alreadyLocked = false)
    {
        using var held = alreadyLocked ? null : new FileStream(Path.Combine(attempt.Tools, ".mbus-applier.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        attempt = attempt with { RecoveryTries = attempt.RecoveryTries + 1, RecoveryInstalled = attempt.RecoveryInstalled || installed };
        Write(Path.Combine(root, "attempt.json"), attempt);
        var repair = await Update(root, attempt, true);
        var recovered = string.Equals(await Version(attempt.Shim), attempt.From, StringComparison.OrdinalIgnoreCase);
        if (!recovered)
        {
            var location = attempt.Global ? "-g" : $"--tool-path \"{attempt.Tools}\"";
            reason += " Recovery failed: " + (repair?.AllOutput ?? "dotnet did not finish.") + $" Run: {attempt.Dotnet} tool update {location} {DotnetToolInstall.PackageId} --version {attempt.From} --allow-downgrade --configfile \"{Path.Combine(root, "Recovery.Config")}\"";
            File.WriteAllText(Path.Combine(root, "last-failure.txt"), Diagnostic(reason));
        }
        Write(Path.Combine(root, "attempt.json"), attempt with { Terminal = recovered || attempt.RecoveryTries >= 3 });
        Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id,
            recovered && attempt.RecoveryInstalled ? RunnerUpdateOutcome.RolledBack : RunnerUpdateOutcome.NotInstalled, Diagnostic(reason), fileInUse));
    }
}

internal sealed record ToolUpdateAttempt(string Id, string From, string To, bool Pinned, string Tools, string Shim, string Dotnet, int Starts, bool Confirmed, int Retries)
{
    public bool Global { get; init; }
    public string? Channel { get; init; }
    public bool Terminal { get; init; }
    public bool RetryPending { get; init; }
    public int RecoveryTries { get; init; }
    public bool RecoveryInstalled { get; init; }
}
internal enum ToolRecoveryAction { Done, Repair, Manual }
internal enum ToolStartAction { None, Apply, CheckInterrupted, CountStart, Recover }
internal sealed record ToolUpdateResult(string Id, RunnerUpdateOutcome Outcome, string? Reason, bool FileInUse)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>The engine's local holder wait, separate from the installation handoff.</summary>
public interface ISessionRunnerUpdater : IRunnerUpdater
{
    string CurrentVersion { get; }
    bool WaitingAgain { get; }
    IReadOnlyList<ToolInstallHolder> Holders(bool final = false);
    void KeepWaiting(UpdateRequest request);
    UpdateRequest? Waiting { get; }
    void ClearWaiting();
    void ConfirmConnected();
}
