using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Service;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;
using NuGet.Versioning;

namespace MessageBus.Cli.Update;

/// <summary>Dotnet's service handoff. Its private applier copy survives replacement of .store.</summary>
public sealed class DotnetToolUpdater(DotnetToolInstall install, string directory, string dotnet, TextWriter log,
    RunnerUpdateRecord record, string currentVersion, TimeProvider time, Func<ProcessTable>? processes = null, string? source = null, string? entryAssembly = null, bool manual = false) : ISessionRunnerUpdater
{
    public string? CurrentVersion => install.Package?.Version;
    internal const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    internal const string PrivateCommand = "__runner-update";
    private const string ServiceExtension = ".service";
    internal string? MinimumServiceVersion { get; init; }
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
            if (request.Version != RunnerUpdates.Latest && !IsReleaseVersion(request.Version))
                throw new IOException("The update needs an exact, valid package version.");
            if (install.ToolsPath is null) throw new IOException("Local manifest tools cannot update through the runner service.");
            var package = install.Package ?? throw new IOException(DotnetToolPackage.IdentityUnknown);
            var shim = install.Shim ?? throw new IOException("The installed dotnet tool's mbus launcher is missing.");
            if (Holders(final: true).Count > 0)
            {
                WaitingAgain = true;
                KeepWaiting(request);
                return false;
            }
            if (request.Version == RunnerUpdates.Latest)
                request = request with { Version = await LatestAsync(MinimumServiceVersion is null
                    ? source ?? Environment.GetEnvironmentVariable("MBUS_UPDATE_NUGET_SOURCE") : null, request.Channel, cancellationToken) };
            if (!IsReleaseVersion(request.Version))
                throw new IOException("The update needs an exact, valid package version.");
            if (MinimumServiceVersion is { } minimum && DotnetToolPackage.Compare(request.Version, minimum) < 0)
                throw new IOException("That package version predates the installed Windows service host.");
            var comparison = DotnetToolPackage.Compare(package.Version, request.Version);
            if (comparison == 0 || (!request.Pinned && comparison > 0))
                throw new IOException(comparison == 0 ? "This version is already installed." : "An unpinned update cannot install an older version.");
            ValidatePaths(OperatingSystem.IsWindows(), Root, install.ToolsPath, dotnet);
            var entry = entryAssembly ?? Assembly.GetEntryAssembly()!.Location;
            using var original = package.OpenRecovery(entry, out var recoverySha512);
            var recovery = Path.Combine(Root, "recovery");
            Directory.CreateDirectory(recovery);
            var savedPackage = Path.Combine(recovery, DotnetToolPackage.ArchiveName(package.Id, package.Version));
            using (var saved = File.Create(savedPackage + ".tmp"))
                await original.CopyToAsync(saved, cancellationToken);
            File.Move(savedPackage + ".tmp", savedPackage, overwrite: true);
            foreach (var old in Directory.EnumerateFiles(recovery, "*.nupkg").Where(path => !DotnetToolInstall.SamePath(path, savedPackage)))
                File.Delete(old);
            var service = MinimumServiceVersion is not null;
            File.WriteAllText(Path.Combine(Root, "NuGet.Config"), NuGetConfig(service ? NuGetOrg
                : source ?? Environment.GetEnvironmentVariable("MBUS_UPDATE_NUGET_SOURCE") ?? NuGetOrg, requireSignature: service));
            // Recovery uses the previously installed package without requiring signing metadata from the network.
            File.WriteAllText(Path.Combine(Root, "Recovery.Config"), NuGetConfig(recovery));
            var assembly = service ? null : CopyApplier(entry);
            var attempt = new ToolUpdateAttempt(Guid.NewGuid().ToString("N"), package.Version, request.Version, request.Pinned,
                install.ToolsPath, shim, dotnet, 0, false,
                Read<ToolUpdateAttempt>(Path.Combine(Root, "attempt.json")) is { } previous && previous.To == request.Version ? previous.Retries : 0);
            foreach (var old in Directory.EnumerateFiles(Root, "applying.*")) File.Delete(old);
            attempt = attempt with { Global = install.Global, Channel = request.Channel, PackageId = package.Id, RecoverySha512 = recoverySha512 };
            Write(Path.Combine(Root, "attempt.json"), attempt);
            File.Delete(Path.Combine(Root, "result.json"));
            File.Delete(Path.Combine(Root, "last-failure.txt"));
            var windows = OperatingSystem.IsWindows();
            var pending = Path.Combine(Root, "pending" + (service ? ServiceExtension : windows ? ".cmd" : ".sh"));
            if (service)
            {
                // SCM's immutable dispatcher owns the applier and runs it after the worker has exited.
                WriteText(pending, "");
            }
            else
            {
                WriteText(Path.Combine(Root, windows ? "wrapper.cmd" : "wrapper.sh"), Script(windows, dotnet, assembly!, Root, "start"));
                WriteText(pending, Script(windows, dotnet, assembly!, Root, "apply"));
            }
            if (Holders(final: true).Count > 0)
            {
                File.Delete(pending);
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
            LastResult = new() { Outcome = RunnerUpdateOutcome.NotInstalled, FromVersion = CurrentVersion ?? currentVersion, ToVersion = request.Version,
                Reason = Diagnostic(ex.Message), At = time.GetUtcNow() };
            record.Save(LastResult, log, install);
            await log.WriteLineAsync("Update not installed: " + LastResult.Reason
                + (ex.InnerException is { } inner ? " (" + Diagnostic(inner.Message) + ")" : ""));
            return false;
        }
    }

    private string CopyApplier(string entry)
    {
        // Copy the framework-dependent tool and its dependencies, never run the applier from .store.
        var copy = Path.Combine(Root, "applier");
        Directory.CreateDirectory(copy);
        foreach (var old in Directory.EnumerateFiles(copy, "*", SearchOption.AllDirectories)) File.Delete(old);
        var programDirectory = Path.GetDirectoryName(entry)!;
        foreach (var file in Directory.EnumerateFiles(programDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(programDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        return Path.Combine(copy, Path.GetFileName(entry));
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
        if (!attempt.Terminal && result.Outcome != RunnerUpdateOutcome.Updated && result.FileInUse && DotnetToolPackage.Compare(CurrentVersion, attempt.From) == 0
            && (Holders().Count > 0 || attempt.Retries == 0))
        {
            if (attempt.RetryPending) return;
            Write(Path.Combine(Root, "attempt.json"), attempt with { Retries = attempt.Retries + (Holders().Count == 0 ? 1 : 0), RetryPending = true });
            KeepWaiting(new() { Version = attempt.To, Pinned = attempt.Pinned, Channel = attempt.Channel, Via = RunnerInstallKind.DotnetTool });
            record.Clear();
            return;
        }
        var outcome = result.Outcome;
        if (outcome == RunnerUpdateOutcome.Updated && DotnetToolPackage.Compare(CurrentVersion, attempt.To) != 0)
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
                && DotnetToolPackage.Compare(CurrentVersion, attempt.To) == 0
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
            try { await ReadRegistrationAsync(source, versions, ct); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new IOException(source is null ? "Couldn't read the list of mbus versions from nuget.org."
                    : "Couldn't read the list of mbus versions from this machine's update source.", ex);
            }
        }
        return SelectLatest(versions, channel) ?? throw new IOException("No listed package version is available from this machine's update source.");
    }

    private static async Task ReadRegistrationAsync(string? source, List<string> versions, CancellationToken ct)
    {
        // Registration metadata includes listed=false; flat-container lists alone cannot exclude pulled releases.
        // nuget.org serves registration5-gz-semver2 gzip-compressed whatever the request asks for.
        using var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
            { Timeout = TimeSpan.FromSeconds(10) };
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

    internal static string? SelectLatest(IEnumerable<string> versions, string? channel)
    {
        var valid = versions.Where(IsReleaseVersion).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidates = channel == "preview" ? valid : valid.Where(v => !v.Split('+')[0].Contains('-')).ToArray();
        if (candidates.Length == 0) candidates = valid;
        return candidates.OrderByDescending(NuGetVersion.Parse).FirstOrDefault();
    }

    internal static bool IsReleaseVersion(string? version) =>
        ReleaseFeed.IsPinnableVersion(version) && NuGetVersion.TryParse(version, out _);

    internal static string NuGetConfig(string source, bool requireSignature = false) => new XDocument(new XElement("configuration",
        new XElement("packageSources", new XElement("clear"), new XElement("add", new XAttribute("key", "update"), new XAttribute("value", source))),
        requireSignature ? new XElement("config", new XElement("add", new XAttribute("key", "signatureValidationMode"), new XAttribute("value", "require"))) : null)).ToString();

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
        && string.Equals(attempt.PackageId, DotnetToolInstall.PackageId, StringComparison.OrdinalIgnoreCase)
        && NuGetVersion.TryParse(attempt.From, out var version) && string.Equals(attempt.From, version.ToNormalizedString(), StringComparison.OrdinalIgnoreCase)
        && IsReleaseVersion(attempt.To) && (attempt.Pinned || DotnetToolPackage.Compare(attempt.To, attempt.From) >= 0);

    /// <summary>Internal service entry point, running entirely outside the tools install.</summary>
    public static Task<int> RunApplierAsync(string mode, string root) => RunApplierAsync(mode, root, null, null, null, RunCommand);

    internal delegate Task<CommandResult?> ToolCommand(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null);

    internal static Task<int> RunServiceApplierAsync(string root, string tools, string dotnet, Func<ProcessTable>? processes = null, ToolCommand? run = null) =>
        RunApplierAsync("start", root, tools, dotnet, processes, RestrictServiceSources(root, dotnet, run ?? RunCommand));

    private static ToolCommand RestrictServiceSources(string root, string dotnet, ToolCommand run) =>
        async (executable, arguments, timeout, environment) =>
        {
            if (!DotnetToolInstall.SamePath(executable, dotnet))
                return await run(executable, arguments, timeout, environment);

            // Update builds the command and selects the config. Keep the verified file open against
            // writes and replacement while the Windows SDK reads it.
            if (arguments is not [_, _, _, _, _, _, _, "--configfile", var config, ..])
                return new CommandResult(1, "", "The Windows service's NuGet command requires an explicit configuration.");
            var expected = Path.GetFileName(config) == "Recovery.Config"
                ? NuGetConfig(Path.Combine(root, "recovery"))
                : NuGetConfig(NuGetOrg, requireSignature: true);
            try
            {
                using var contents = File.OpenRead(config);
                using var reader = new StreamReader(contents);
                if (await reader.ReadToEndAsync() == expected)
                    return await run(executable, arguments, timeout, environment);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CommandResult(1, "", "Could not read the Windows service's NuGet configuration.");
            }
            return new CommandResult(1, "", "The Windows service's NuGet configuration did not match its restricted source.");
        };

    internal static Task<CommandResult?> RunCommand(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment) =>
        CommandRunner.RunAsync(executable, arguments, CancellationToken.None, timeout, environment);

    private static async Task<int> RunApplierAsync(string mode, string root, string? serviceTools, string? serviceDotnet,
        Func<ProcessTable>? processes, ToolCommand run)
    {
        try
        {
            var path = Path.Combine(root, "attempt.json");
            var attempt = Read<ToolUpdateAttempt>(path);
            if (attempt is null || !Valid(attempt)) return 1;
            if (serviceTools is not null && (attempt.Global || !DotnetToolInstall.SamePath(attempt.Dotnet, serviceDotnet!)
                || !DotnetToolInstall.SamePath(attempt.Tools, serviceTools)
                || !(DotnetToolInstall.SamePath(attempt.Shim, Path.Combine(serviceTools, "mbus.exe"))
                     || DotnetToolInstall.SamePath(attempt.Shim, Path.Combine(serviceTools, "mbus.cmd")))))
                return 1;
            ValidatePaths(OperatingSystem.IsWindows(), root, attempt.Tools, attempt.Shim, attempt.Dotnet);
            if (mode == "cli" || mode.StartsWith("cli-wait-", StringComparison.Ordinal))
            {
                if (mode != "cli")
                {
                    if (!int.TryParse(mode[9..], out var pid) || pid <= 0) return 1;
                    try { using var parent = System.Diagnostics.Process.GetProcessById(pid); await parent.WaitForExitAsync(); }
                    catch (ArgumentException) { } // It already exited before we opened it.
                }
                await Apply(root, attempt, run, processes, printOutput: true);
                var result = Read<ToolUpdateResult>(Path.Combine(root, "result.json"));
                if (result?.Outcome == RunnerUpdateOutcome.Updated)
                {
                    Console.WriteLine($"mbus {attempt.To} is installed.");
                    return 0;
                }
                Console.Error.WriteLine("Update not installed: " + result?.Reason);
                return 1;
            }
            if (mode == "apply") { await Apply(root, attempt, run, processes); return 0; }
            if (mode != "start") return 1;
            // OS file locking releases on a crash, unlike a stale directory lock.
            using var held = new FileStream(Path.Combine(attempt.Tools, ".mbus-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var extension = serviceTools is not null ? ServiceExtension : OperatingSystem.IsWindows() ? ".cmd" : ".sh";
            var pending = Path.Combine(root, "pending" + extension);
            var applying = Path.Combine(root, "applying." + attempt.Id + extension);
            if (attempt.Terminal || attempt.RetryPending) return 0;
            if (StartDecision(attempt, Read<ToolUpdateResult>(Path.Combine(root, "result.json")), File.Exists(pending), File.Exists(applying)) == ToolStartAction.Apply)
            {
                File.Move(pending, applying, true); // rename before applying so interruption can be recovered
                if (serviceTools is not null) await Apply(root, attempt, run, processes);
                else if (OperatingSystem.IsWindows())
                    _ = await CommandRunner.RunAsync("cmd.exe", [], CancellationToken.None, TimeSpan.FromMinutes(6), commandLine: "/d /s /c \"\"" + applying + "\"\"");
                else _ = await CommandRunner.RunAsync("/bin/sh", [applying], CancellationToken.None, TimeSpan.FromMinutes(6));
                if (Read<ToolUpdateResult>(Path.Combine(root, "result.json"))?.Id != attempt.Id)
                {
                    if (DotnetToolPackage.Compare(await Version(attempt, run), attempt.To) == 0)
                        Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.Updated, null, false));
                    else await Repair(root, attempt, run, "The update process did not finish; restoring the previous version.", false);
                }
            }
            else if (StartDecision(attempt, Read<ToolUpdateResult>(Path.Combine(root, "result.json")), false, File.Exists(applying)) == ToolStartAction.CheckInterrupted)
            {
                var running = await Version(attempt, run);
                if (DotnetToolPackage.Compare(running, attempt.To) == 0)
                    Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.Updated, null, false));
                else if (DotnetToolPackage.Compare(running, attempt.From) == 0)
                {
                    Write(path, attempt with { Terminal = true });
                    Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, "The interrupted update was not installed.", false));
                }
                else await Repair(root, attempt, run, "The update was interrupted; restoring the previous version.", false);
            }
            attempt = Read<ToolUpdateAttempt>(path) ?? attempt;
            var startupResult = Read<ToolUpdateResult>(Path.Combine(root, "result.json"));
            if (StartDecision(attempt, startupResult, false, false) == ToolStartAction.CountStart)
            {
                attempt = attempt with { Starts = attempt.Starts + 1 };
                Write(path, attempt);
                if (attempt.Starts >= 3 && DotnetToolPackage.Compare(await Version(attempt, run), attempt.To) == 0)
                    await Repair(root, attempt, run, "The updated runner did not confirm a connection after three starts.", true);
            }
            else if (StartDecision(attempt, startupResult, false, false) == ToolStartAction.Recover)
            {
                var running = await Version(attempt, run);
                switch (RecoveryDecision(attempt, running))
                {
                    case ToolRecoveryAction.Repair:
                        await Repair(root, attempt, run, "Retrying interrupted recovery.", false);
                        break;
                    default:
                        Write(path, attempt with { Terminal = true });
                        var previous = DotnetToolPackage.Compare(running, attempt.From) == 0;
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
        if (DotnetToolPackage.Compare(running, attempt.From) == 0) return ToolRecoveryAction.Done;
        if (running is null || (DotnetToolPackage.Compare(running, attempt.To) == 0
            && attempt.RecoveryInstalled && attempt.Starts >= 3)) return ToolRecoveryAction.Repair;
        return ToolRecoveryAction.Manual;
    }

    private static IReadOnlyList<ToolInstallHolder> ApplierHolders(ToolUpdateAttempt attempt, Func<ProcessTable>? processes) =>
        ToolInstallHolders.FinalCheck(new(attempt.Tools, attempt.Global), processes?.Invoke() ?? ProcessTable.ForThisMachine(), Environment.ProcessId,
            new HashSet<int> { Environment.ProcessId });

    private static Task<CommandResult?> CheckShim(ToolUpdateAttempt attempt, ToolCommand run) =>
        run(attempt.Shim, ["--version"], TimeSpan.FromSeconds(30));

    private static string? InstalledVersion(ToolUpdateAttempt attempt) => DotnetToolPackage.InstalledVersion(attempt.Tools, attempt.PackageId);

    private static async Task<string?> Version(ToolUpdateAttempt attempt, ToolCommand run)
    {
        var result = await CheckShim(attempt, run);
        return result?.ExitCode == 0 ? InstalledVersion(attempt) : null;
    }
    private static async Task<CommandResult?> Update(string root, ToolUpdateAttempt attempt, ToolCommand run, bool recovery)
    {
        List<string> args = ["tool", "update", .. attempt.Global ? new[] { "-g" } : new[] { "--tool-path", attempt.Tools }, attempt.PackageId, "--version", recovery ? attempt.From : attempt.To,
            "--configfile", Path.Combine(root, recovery ? "Recovery.Config" : "NuGet.Config")];
        if (recovery || attempt.Pinned) args.Add("--allow-downgrade");
        return await run(attempt.Dotnet, args, TimeSpan.FromMinutes(2),
            new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "1", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" });
    }

    private static string? CheckSavedRecovery(string root, ToolUpdateAttempt attempt)
    {
        if (string.IsNullOrEmpty(attempt.RecoverySha512))
            return "This update attempt has no recorded SHA-512 checksum for its saved recovery package.";
        var path = Path.Combine(root, "recovery", DotnetToolPackage.ArchiveName(attempt.PackageId, attempt.From));
        try
        {
            using var archive = File.OpenRead(path);
            return DotnetToolPackage.Checksum(archive) == attempt.RecoverySha512 ? null
                : $"The saved recovery package for {attempt.PackageId} {attempt.From} does not match its recorded SHA-512 checksum.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The saved recovery package for {attempt.PackageId} {attempt.From} could not be read. {ex.Message}";
        }
    }

    private static async Task Apply(string root, ToolUpdateAttempt attempt, ToolCommand run, Func<ProcessTable>? processes, bool printOutput = false)
    {
        using var held = new FileStream(Path.Combine(attempt.Tools, ".mbus-applier.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ApplierHolders(attempt, processes).Count > 0)
        {
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, "Processes are using this install.", true));
            return;
        }
        var current = InstalledVersion(attempt);
        if (DotnetToolPackage.Compare(current, attempt.From) != 0)
        {
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled,
                current is null ? DotnetToolPackage.IdentityUnknown : $"The installed tool changed to {current} after this update was prepared.", false));
            return;
        }
        if (CheckSavedRecovery(root, attempt) is { } recoveryFailure)
        {
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled,
                Diagnostic(recoveryFailure + " Prepare the update again to stage a verified recovery package."), false));
            return;
        }
        var update = await Update(root, attempt, run, false);
        if (printOutput && update is not null) Console.WriteLine(update.AllOutput);
        var check = await CheckShim(attempt, run);
        current = InstalledVersion(attempt);
        var running = check?.ExitCode == 0 ? current : null;
        // SDKs can report failure after replacement. Check the installed entry and that mbus starts.
        if (DotnetToolPackage.Compare(running, attempt.To) == 0)
        {
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.Updated, null, false));
            return;
        }
        var reason = update?.AllOutput ?? "dotnet did not start or did not finish within two minutes.";
        if (check?.ExitCode != 0) reason += " New tool failed its start check: " + (check?.AllOutput ?? "The tool did not start or did not finish within 30 seconds.");
        else if (running is null) reason += " " + DotnetToolPackage.IdentityUnknown;
        var fileInUse = reason.Contains("denied", StringComparison.OrdinalIgnoreCase) || reason.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("in use", StringComparison.OrdinalIgnoreCase) || ApplierHolders(attempt, processes).Count > 0;
        // A failed start check only counts as a rollback if the new version actually installed.
        var installed = DotnetToolPackage.Compare(current, attempt.To) == 0;
        if (DotnetToolPackage.Compare(running, attempt.From) == 0)
        {
            Write(Path.Combine(root, "attempt.json"), attempt with { Terminal = !fileInUse });
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, Diagnostic(reason), fileInUse));
        }
        else await Repair(root, attempt, run, reason, installed, fileInUse, alreadyLocked: true);
    }
    private static async Task Repair(string root, ToolUpdateAttempt attempt, ToolCommand run, string reason, bool installed, bool fileInUse = false, bool alreadyLocked = false)
    {
        using var held = alreadyLocked ? null : new FileStream(Path.Combine(attempt.Tools, ".mbus-applier.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (CheckSavedRecovery(root, attempt) is { } recoveryFailure)
        {
            reason = Diagnostic($"Recovery was refused. {recoveryFailure} Restore {attempt.PackageId} {attempt.From} from a trusted NuGet source.");
            File.WriteAllText(Path.Combine(root, "last-failure.txt"), reason);
            Write(Path.Combine(root, "attempt.json"), attempt with { Terminal = true });
            Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id, RunnerUpdateOutcome.NotInstalled, reason, false));
            return;
        }
        attempt = attempt with { RecoveryTries = attempt.RecoveryTries + 1, RecoveryInstalled = attempt.RecoveryInstalled || installed };
        Write(Path.Combine(root, "attempt.json"), attempt);
        var repair = await Update(root, attempt, run, true);
        var recovered = DotnetToolPackage.Compare(await Version(attempt, run), attempt.From) == 0;
        if (!recovered)
        {
            var location = attempt.Global ? "-g" : $"--tool-path \"{attempt.Tools}\"";
            reason += " Recovery failed: " + (repair?.AllOutput ?? "dotnet did not finish.") + $" Run: {attempt.Dotnet} tool update {location} {attempt.PackageId} --version {attempt.From} --allow-downgrade --configfile \"{Path.Combine(root, "Recovery.Config")}\"";
            File.WriteAllText(Path.Combine(root, "last-failure.txt"), Diagnostic(reason));
        }
        Write(Path.Combine(root, "attempt.json"), attempt with { Terminal = recovered || attempt.RecoveryTries >= 3 });
        Write(Path.Combine(root, "result.json"), new ToolUpdateResult(attempt.Id,
            recovered && attempt.RecoveryInstalled ? RunnerUpdateOutcome.RolledBack : RunnerUpdateOutcome.NotInstalled, Diagnostic(reason), fileInUse));
    }
}

internal sealed record ToolUpdateAttempt(string Id, string From, string To, bool Pinned, string Tools, string Shim, string Dotnet, int Starts, bool Confirmed, int Retries)
{
    public string PackageId { get; init; } = DotnetToolInstall.PackageId;
    public string? RecoverySha512 { get; init; }
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
    string? CurrentVersion { get; }
    bool WaitingAgain { get; }
    IReadOnlyList<ToolInstallHolder> Holders(bool final = false);
    void KeepWaiting(UpdateRequest request);
    UpdateRequest? Waiting { get; }
    void ClearWaiting();
    void ConfirmConnected();
}
