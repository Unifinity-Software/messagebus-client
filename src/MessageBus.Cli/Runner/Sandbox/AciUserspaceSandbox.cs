using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace MessageBus.Cli.Runner.Sandbox;

// The OCI runtime's state and launch plans stay outside every guest mount.
// Only this image-owned helper may interpret a plan or recover its journal.
internal sealed class AciUserspaceSandbox
{
    private const string Helper = "/usr/local/lib/mbus/aci-sandbox.mjs";
    private const string Runtime = "/usr/local/lib/mbus/gvisor/runsc";
    private const string Node = "/usr/local/bin/node";
    private const string QuarantineMessage = "Protected ACI cleanup is unverified. This runner is quarantined until its owned runtime journal is recovered.";
    private readonly string _root;
    private readonly FileStream _ownerLock;
    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);
    private string? _problem;

    private sealed class Run
    {
        internal readonly object Gate = new();
        internal readonly List<string> Launches = [];
        internal bool CheckoutClosed;
        internal bool Ending;
    }

    private AciUserspaceSandbox(string root, FileStream ownerLock)
    {
        _root = root;
        _ownerLock = ownerLock;
    }

    internal string? Problem => Volatile.Read(ref _problem);

    internal static string? PersistedQuarantine(RunIsolationOptions options)
    {
        var settings = options.SettingsDirectory ?? Path.Combine(options.HomeDirectory, ".mbus");
        var root = Path.Combine(settings, "aci-runtime");
        try
        {
            return File.Exists(Path.Combine(root, "quarantined"))
                || Directory.Exists(root) && Directory.EnumerateDirectories(root, "launch-*").Any()
                ? QuarantineMessage : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return QuarantineMessage; }
    }

    internal static AciUserspaceSandbox? TryCreate(RunIsolationOptions options, out string problem)
    {
        problem = "This ACI image needs the pinned userspace sandbox runtime and helper for private procfs.";
        if (!OperatingSystem.IsLinux()
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64
            || !File.Exists(Runtime) || !File.Exists(Helper)) return null;
        var settings = MacSandbox.RealPath(options.SettingsDirectory ?? Path.Combine(options.HomeDirectory, ".mbus"));
        var root = Path.Combine(settings, "aci-runtime");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (MacSandbox.RealPath(root) != root) return null;
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream owner;
        try { owner = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { problem = "Another runner owns this ACI runtime journal."; return null; }
        var sandbox = new AciUserspaceSandbox(root, owner);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "launch-*"))
            {
                if (!ValidLaunch(directory) || !sandbox.CleanupAsync(directory).GetAwaiter().GetResult())
                {
                    sandbox.Quarantine();
                    problem = sandbox.Problem!;
                    owner.Dispose();
                    return null;
                }
                Directory.Delete(directory, recursive: true);
            }
            File.Delete(Path.Combine(root, "quarantined"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            sandbox.Quarantine();
            owner.Dispose();
            problem = sandbox.Problem!;
            return null;
        }
        problem = "";
        return sandbox;
    }

    private static bool ValidLaunch(string directory) => Path.GetFileName(directory) is { Length: 39 } name
        && name.StartsWith("launch-", StringComparison.Ordinal) && name[7..].All(Uri.IsHexDigit)
        && MacSandbox.RealPath(directory) == directory;

    internal void Prepare(string run) => _runs.TryAdd(run, new Run());

    internal bool EnforcesFilePolicy(string run) => Problem is null && _runs.ContainsKey(run);

    internal ProcessLaunch Wrap(ProcessLaunch launch, string run, string sockets, string work, bool canEditFiles)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (Problem is { } problem) throw new IOException(problem);
        var state = _runs.GetOrAdd(run, _ => new Run());
        lock (state.Gate)
        {
            if (state.Ending || launch.TrustedCheckout && state.CheckoutClosed)
                throw new IOException("The protected checkout phase has ended.");
            if (!launch.TrustedCheckout) state.CheckoutClosed = true;
            // Every source is a guest mountpoint, including a writable worktree.
            // Its host parent has no other writable guest alias, so a task cannot
            // swap this mount source before another concurrent tool launch.
            Directory.CreateDirectory(work);
            if (work != Path.Combine(run, "work") || MacSandbox.RealPath(run) != run
                || MacSandbox.RealPath(work) != work || MacSandbox.RealPath(sockets) != sockets)
                throw new IOException("The protected run has a noncanonical mount source.");
            var directory = Path.Combine(_root, "launch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            state.Launches.Add(directory);
            var plan = new Dictionary<string, object?>
            {
                ["run"] = run, ["work"] = work, ["sockets"] = sockets,
                ["cwd"] = launch.WorkingDirectory, ["file"] = launch.FileName, ["args"] = launch.Arguments,
                ["env"] = GuestEnvironment(launch), ["readOnly"] = !canEditFiles && !launch.TrustedCheckout,
            };
            var file = Path.Combine(directory, "plan.json");
            try
            {
                using (var stream = new FileStream(file, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
                    JsonSerializer.Serialize(stream, plan);
                using var prepared = new FileStream(Path.Combine(directory, "prepared"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch
            {
                // No ProcessLaunch escaped, so no helper can have claimed it.
                state.Launches.Remove(directory);
                Directory.Delete(directory, recursive: true);
                throw;
            }
            return launch with
            {
                FileName = Node, Arguments = [Helper, "run", directory], RawArguments = null,
                Environment = RunEnvironmentScrubber.Build(Environment.GetEnvironmentVariables(), [],
                    new Dictionary<string, string?> { ["PATH"] = "/usr/local/bin:/usr/bin:/bin", ["LC_ALL"] = "C" }),
            };
        }
    }

    private static Dictionary<string, string?> GuestEnvironment(ProcessLaunch launch)
    {
        // Reproduce AgentProcess's environment exactly: inherited values allowed
        // by the existing scrubber, no inherited Codex identity, then task values.
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string name && RunEnvironmentScrubber.IsAllowed(name, []))
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        MessageBus.Cli.Agents.CodexAuthentication.RemoveKeys(environment);
        foreach (var (name, value) in launch.Environment)
        {
            if (value is null) environment.Remove(name);
            else environment[name] = value;
        }
        return environment;
    }

    internal async Task EndAsync(string run)
    {
        if (!_runs.TryGetValue(run, out var state)) return;
        string[] launches;
        lock (state.Gate) { state.Ending = true; launches = [.. state.Launches]; }
        var safe = true;
        try
        {
            foreach (var directory in launches)
            {
                if (await CleanupAsync(directory).ConfigureAwait(false)) Directory.Delete(directory, recursive: true);
                else safe = false;
            }
        }
        catch
        {
            Quarantine();
            throw;
        }
        if (!safe)
        {
            Quarantine();
            throw new IOException(Problem);
        }
        _runs.TryRemove(run, out _);
        GC.KeepAlive(_ownerLock);
    }

    private async Task<bool> CleanupAsync(string directory)
    {
        if (!ValidLaunch(directory)) return false;
        var info = new ProcessStartInfo(Node)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        info.ArgumentList.Add(Helper);
        info.ArgumentList.Add("cleanup");
        info.ArgumentList.Add(directory);
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        info.Environment["LC_ALL"] = "C";
        try
        {
            using var process = Process.Start(info);
            if (process is null) return false;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                return false;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private void Quarantine()
    {
        Volatile.Write(ref _problem, QuarantineMessage);
        File.WriteAllText(Path.Combine(_root, "quarantined"), "cleanup-unverified");
    }
}
