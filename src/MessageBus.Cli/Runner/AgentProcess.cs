using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MessageBus.Cli.Agents;

namespace MessageBus.Cli.Runner;

/// <summary>A process to start for a run.</summary>
public sealed record ProcessLaunch
{
    /// <summary>Full path of the executable.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments, one per item (quoted for the platform here).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Windows only: the command line after the executable, exactly as given (instead of <see cref="Arguments"/>).</summary>
    public string? RawArguments { get; init; }

    /// <summary>Working directory.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Variables added to (or, when null, removed from) the inherited environment.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>Written to stdin, which is then closed; null closes stdin at once.</summary>
    public string? StandardInput { get; init; }

    // Set only by the runner's checkout implementation. Delivery setup and agent
    // commands must retain their role's file restrictions.
    internal bool TrustedCheckout { get; init; }

    // Captured before an OS wrapper replaces its host environment. Never serialized
    // into launch plans, arguments or helper environment variables.
    internal Func<string, string>? OutputFilter { get; init; }
}

/// <summary>
/// One child process of a run, started the same way on Windows, macOS and Linux: stdin, stdout and
/// stderr redirected as UTF-8, stdout read line by line as it arrives. <see cref="Kill"/> ends the
/// whole process tree (an agent's shell commands and their children too).
/// </summary>
public sealed class AgentProcess : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly Process _process;
    private readonly Task _readers;
    private readonly Queue<string> _tail = new();
    private readonly int _tailLines;

    private AgentProcess(Process process, Action<string>? onOutputLine, int tailLines, string? input, Func<string, string>? filterOutput)
    {
        _process = process;
        _tailLines = tailLines;
        var stdout = Task.Run(() => PumpAsync(process.StandardOutput, onOutputLine, filterOutput));
        var stderr = Task.Run(() => PumpAsync(process.StandardError, null, filterOutput));
        _readers = Task.WhenAll(stdout, stderr);
        _ = WriteInputAsync(process.StandardInput, input);
    }

    /// <summary>The process id.</summary>
    public int Id => _process.Id;

    /// <summary>Starts <paramref name="launch"/>.</summary>
    /// <param name="launch">What to start.</param>
    /// <param name="onOutputLine">Called for each stdout line, on a background thread.</param>
    /// <param name="tailLines">How many of the last output lines (stdout and stderr) to keep.</param>
    /// <exception cref="Win32Exception">The executable couldn't be started.</exception>
    public static AgentProcess Start(ProcessLaunch launch, Action<string>? onOutputLine = null, int tailLines = 200) =>
        Start(launch, onOutputLine, tailLines, filterOutput: null);

    internal static AgentProcess Start(ProcessLaunch launch, Action<string>? onOutputLine, int tailLines, Func<string, string>? filterOutput)
    {
        filterOutput ??= launch.OutputFilter;
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = launch.WorkingDirectory,
        };
        if (OperatingSystem.IsWindows() && launch.RawArguments is { } raw)
        {
            info.FileName = launch.FileName;
            info.Arguments = raw;
        }
        else if (OperatingSystem.IsWindows() && IsBatchFile(launch.FileName)
            && CmdShim.Resolve(launch.FileName, name => Variable(launch, name)) is { } shim)
        {
            // the original issue: npm installs agents on Windows as .cmd shims that run node with the package's script. Starting
            // that directly passes every argument as it is; through cmd.exe, " and % can't be passed safely.
            info.FileName = shim.Program;
            foreach (var arg in shim.Arguments.Concat(launch.Arguments))
            {
                info.ArgumentList.Add(arg);
            }
        }
        else if (OperatingSystem.IsWindows() && IsBatchFile(launch.FileName))
        {
            // Any other batch file only cmd.exe can run. /s keeps the quoting below intact: cmd strips
            // the outer pair and runs the rest as written.
            info.FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /s /c \"" + string.Join(' ', new[] { launch.FileName }.Concat(launch.Arguments).Select(QuoteForCmd)) + "\"";
        }
        else
        {
            info.FileName = launch.FileName;
            foreach (var arg in launch.Arguments)
            {
                info.ArgumentList.Add(arg);
            }
        }
        CodexAuthentication.RemoveKeys(info.Environment);
        foreach (var (name, value) in launch.Environment)
        {
            if (value is null)
            {
                info.Environment.Remove(name);
            }
            else
            {
                info.Environment[name] = value;
            }
        }

        var process = Process.Start(info) ?? throw new Win32Exception($"Couldn't start {launch.FileName}.");
        return new AgentProcess(process, onOutputLine, tailLines, launch.StandardInput, filterOutput);
    }

    /// <summary>
    /// Waits for the process to exit, then (briefly) for its output to be read. A grandchild that
    /// outlived the tree and still holds the output open doesn't keep the run waiting.
    /// </summary>
    /// <returns>The exit code.</returns>
    public Task<int> WaitForExitAsync() => WaitForExitAsync(onExit: null);

    internal async Task<int> WaitForExitAsync(Action? onExit)
    {
        await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        onExit?.Invoke();
        try
        {
            await _readers.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        return _process.ExitCode;
    }

    /// <summary>Ends the process and everything it started. Safe to call more than once, or after it exited.</summary>
    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>The last output lines (stdout and stderr interleaved as read).</summary>
    public IReadOnlyList<string> Tail()
    {
        lock (_tail)
        {
            return [.. _tail];
        }
    }

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();

    /// <summary>A shell command line: <c>cmd.exe /d /s /c "…"</c> on Windows, <c>/bin/sh -c …</c> elsewhere.</summary>
    public static ProcessLaunch Shell(string command, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null) =>
        OperatingSystem.IsWindows()
            ? new ProcessLaunch
            {
                FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                // With /s, cmd strips the outer quotes and runs the command exactly as written.
                RawArguments = "/d /s /c \"" + command + "\"",
                WorkingDirectory = workingDirectory,
                Environment = environment ?? new Dictionary<string, string?>(),
            }
            : new ProcessLaunch
            {
                FileName = "/bin/sh",
                Arguments = ["-c", command],
                WorkingDirectory = workingDirectory,
                Environment = environment ?? new Dictionary<string, string?>(),
            };

    private async Task PumpAsync(StreamReader reader, Action<string>? onLine, Func<string, string>? filterOutput)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = filterOutput?.Invoke(line) ?? line;
                lock (_tail)
                {
                    _tail.Enqueue(line);
                    while (_tail.Count > _tailLines)
                    {
                        _tail.Dequeue();
                    }
                }
                onLine?.Invoke(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task WriteInputAsync(StreamWriter stdin, string? input)
    {
        try
        {
            if (input is not null)
            {
                await stdin.WriteAsync(input).ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process exited before reading its input.
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A variable as the process will see it: the launch's own value (null when it removes it), else this process's.</summary>
    private static string? Variable(ProcessLaunch launch, string name)
    {
        foreach (var (key, value) in launch.Environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return System.Environment.GetEnvironmentVariable(name);
    }

    private static bool IsBatchFile(string file) =>
        file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    private static string QuoteForCmd(string arg)
    {
        if (arg.Contains('"') || arg.Contains('%') || arg.Contains('\n') || arg.Contains('\r'))
        {
            throw new ArgumentException($"Can't pass this argument through a .cmd shim safely: {arg}", nameof(arg));
        }
        // A trailing backslash would escape the closing quote for the program behind the shim.
        return "\"" + (arg.EndsWith('\\') ? arg + "\\" : arg) + "\"";
    }
}
