using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MessageBus.Cli.Runner;

/// <summary>
/// Who starts the new mbus after the runner updated itself. Under a service manager (launchd, systemd, Task Scheduler,
/// <c>mbus runner install</c>, a container) the runner exits 75 and the service manager starts the new version, as before.
/// A runner started by hand in a terminal has nobody to do that, so it starts the new version itself, with the same
/// arguments and environment, in the same terminal, and stays behind only to pass on its exit code (and Ctrl+C). The
/// new runner is told it is supervised, so a later update makes it exit 75 and this first process starts the next one:
/// one small waiting process, however many updates follow.
/// </summary>
public static class RunnerRestart
{
    /// <summary>
    /// <c>1</c>: something restarts this runner when it exits 75 (your own script or supervisor); <c>0</c>: it starts the
    /// new version itself even if it looks like it runs under a service manager. Unset: it decides (<see cref="IsSupervised"/>).
    /// </summary>
    public const string SupervisedEnvVar = "MBUS_RUNNER_SUPERVISED";

    // Service managers that start the runner directly (Linux names; systemd --user isn't pid 1). launchd, init and a
    // container's init are pid 1.
    private static readonly HashSet<string> Supervisors = new(StringComparer.Ordinal) { "systemd", "launchd", "supervisord", "runsv", "s6-supervise", "tini" };

    private static Pending? _pending;

    /// <summary>
    /// Whether a service manager restarts this runner after it exits 75, judged when it starts (a runner left running
    /// after its terminal closed is later adopted by pid 1, which doesn't make it a service).
    /// </summary>
    /// <param name="writesLogFile">Started with <c>--log-file</c>, which <c>mbus runner install</c> always passes.</param>
    /// <param name="env">The runner's environment.</param>
    /// <param name="parent">Its parent process (id and name) on macOS and Linux; null where that isn't known (Windows).</param>
    public static bool IsSupervised(bool writesLogFile, Func<string, string?> env, (int Id, string? Name)? parent)
    {
        switch (env(SupervisedEnvVar)?.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "yes":
                return true;
            case "0" or "false" or "no":
                return false;
        }
        return writesLogFile || parent is { } p && (p.Id <= 1 || (p.Name is { } name && Supervisors.Contains(name)));
    }

    /// <summary>This process's parent (id, and its name on Linux), or null on Windows.</summary>
    public static (int Id, string? Name)? ParentProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            var id = GetParentProcessId();
            string? name = null;
            if (OperatingSystem.IsLinux() && File.Exists($"/proc/{id}/comm"))
            {
                name = File.ReadAllText($"/proc/{id}/comm").Trim();
            }
            return (id, name);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Asks <see cref="AfterExitAsync"/> to start <paramref name="executable"/> with the same arguments once the runner has stopped.</summary>
    /// <param name="executable">The freshly installed mbus.</param>
    /// <param name="log">Where the runner writes its status lines.</param>
    public static void Request(string executable, TextWriter log) => _pending = new Pending(executable, log);

    /// <summary>
    /// Program.cs, after the command finished: when the runner asked for it (<see cref="Request"/>) and exited 75,
    /// starts the new mbus with <paramref name="args"/> and returns its exit code; starts it again each time it updates
    /// itself (exits 75 and the file changed). Otherwise returns <paramref name="exitCode"/>.
    /// </summary>
    /// <param name="exitCode">The command's exit code.</param>
    /// <param name="args">The command line, as this process got it.</param>
    public static async Task<int> AfterExitAsync(int exitCode, string[] args)
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } pending || exitCode != RunnerCommand.UpdateExitCode)
        {
            return exitCode;
        }
        // Ctrl+C reaches the new runner too (same console, same process group): let it stop, then pass on its exit code.
        ConsoleCancelEventHandler keepWaiting = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += keepWaiting;
        try
        {
            while (true)
            {
                // the original issue: the version mbus names now, started from its own file (on Windows, rather than through mbus.exe).
                var executable = Current(pending.Executable);
                var before = Stamp(executable);
                await pending.Log.WriteLineAsync($"Starting the new mbus with the same arguments: {executable}").ConfigureAwait(false);
                await pending.Log.FlushAsync().ConfigureAwait(false);
                var code = await RunAsync(executable, args).ConfigureAwait(false);
                var now = Current(pending.Executable);
                if (code != RunnerCommand.UpdateExitCode || (now == executable && Stamp(now) == before))
                {
                    return code;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= keepWaiting;
        }
    }

    private static async Task<int> RunAsync(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment[SupervisedEnvVar] = "1";
        using var child = Process.Start(start) ?? throw new CliException($"Couldn't start {executable}.");
        // `kill <pid>` of the first runner stops the one doing the work.
        using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            _ = Kill(child.Id, 15);
        });
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
    }

    private static string Current(string executable) => Update.VersionedInstall.Current(executable) ?? executable;

    private static (long, DateTime)? Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
    }

    private sealed record Pending(string Executable, TextWriter Log);

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
}
