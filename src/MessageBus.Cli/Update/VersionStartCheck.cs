using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Releases;

namespace MessageBus.Cli.Update;

/// <summary>
/// The updater's check that the mbus it just put in place works: runs <c>&lt;path&gt; --version</c> with no input and its
/// output captured, and wants exit code 0 and the expected version within <see cref="Timeout"/>. When it doesn't, the
/// answer says what happened (couldn't be started, exit code or signal, still running, another version) with the first
/// lines it printed, so a rolled-back update explains itself in <c>mbus update</c>, the runner's log and the Runners page.
/// </summary>
public static partial class VersionStartCheck
{
    /// <summary>How long the new mbus gets to print its version.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

    private const int MaxLines = 3;
    private const int MaxLineLength = 160;
    private const int KeptChars = 8 * 1024;

    /// <summary>Runs the check with <see cref="Timeout"/>.</summary>
    /// <param name="path">The new binary.</param>
    /// <param name="version">The version it should report.</param>
    /// <param name="cancellationToken">Stops the check (the program is stopped too).</param>
    /// <returns>Null when it started and reported <paramref name="version"/>; else why not, starting "it …".</returns>
    public static Task<string?> RunAsync(string path, string version, CancellationToken cancellationToken) =>
        RunAsync(path, version, Timeout, cancellationToken);

    /// <inheritdoc cref="RunAsync(string, string, CancellationToken)"/>
    /// <param name="path">The new binary.</param>
    /// <param name="version">The version it should report.</param>
    /// <param name="timeout">How long it may take.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    internal static async Task<string?> RunAsync(string path, string version, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var code = ex is Win32Exception { NativeErrorCode: not 0 } native ? $", error {native.NativeErrorCode}" : "";
            return $"it couldn't be started ({OneLine(ex.Message)}{code})";
        }

        var output = new Capture();
        var errors = new Capture();
        var reading = Task.WhenAll(output.ReadAsync(process.StandardOutput), errors.ReadAsync(process.StandardError));
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It already exited and closed its end.
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await reading.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            await StopAsync(process).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return $"it was still running after {Seconds(timeout)}, so it was stopped{Printed(output, errors)}";
        }
        catch (OperationCanceledException)
        {
            // It exited, but something it started still holds its output open: judge it by what it printed so far.
            cancellationToken.ThrowIfCancellationRequested();
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        if (process.ExitCode != 0)
        {
            return $"{DescribeExit(process.ExitCode, OperatingSystem.IsWindows() ? null : OperatingSystem.IsMacOS())} after {Seconds(elapsed)}{Printed(output, errors)}";
        }
        var reported = output.Text.Trim();
        if (ReleaseVersion.Compare(reported, version) == 0)
        {
            return null;
        }
        var stderr = Lines(errors.Text) is { Length: > 0 } e ? $"; errors: {e}" : "";
        return reported.Length == 0
            ? $"it exited without printing its version{stderr}"
            : $"it printed {Lines(reported)} instead of its version {version}{stderr}";
    }

    /// <summary>"it exited with code 3", adding the signal for 128 + n on macOS and Linux (how .NET reports a program a signal stopped).</summary>
    /// <param name="code">The exit code.</param>
    /// <param name="macOS">True on macOS, false on Linux, null on Windows (no signals).</param>
    internal static string DescribeExit(int code, bool? macOS)
    {
        var text = $"it exited with code {code}";
        if (macOS is not { } mac || code is <= 128 or > 128 + 64)
        {
            return text;
        }
        var signal = code - 128;
        var name = SignalName(signal, mac);
        text += name is null ? $" (signal {signal})" : $" (signal {signal}, {name})";
        // macOS kills a program whose code signature it refuses before it runs a line: the usual SIGKILL there.
        return signal == 9 && mac ? text[..^1] + "; on macOS often a refused code signature)" : text;
    }

    private static string? SignalName(int signal, bool macOS) => signal switch
    {
        1 => "SIGHUP",
        2 => "SIGINT",
        3 => "SIGQUIT",
        4 => "SIGILL",
        5 => "SIGTRAP",
        6 => "SIGABRT",
        7 when !macOS => "SIGBUS",
        8 => "SIGFPE",
        9 => "SIGKILL",
        10 when macOS => "SIGBUS",
        11 => "SIGSEGV",
        12 when macOS => "SIGSYS",
        13 => "SIGPIPE",
        14 => "SIGALRM",
        15 => "SIGTERM",
        _ => null,
    };

    private static async Task StopAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It exited in the meantime.
        }
    }

    private static string Printed(Capture output, Capture errors)
    {
        var parts = new List<string>(2);
        if (Lines(output.Text) is { Length: > 0 } o)
        {
            parts.Add($"output: {o}");
        }
        if (Lines(errors.Text) is { Length: > 0 } e)
        {
            parts.Add($"errors: {e}");
        }
        return parts.Count == 0 ? "; it printed nothing" : "; " + string.Join("; ", parts);
    }

    /// <summary>The first few non-empty lines, quoted, shortened and joined with " / "; empty when there are none.</summary>
    private static string Lines(string text)
    {
        var lines = text.Split('\n').Select(OneLine).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return "";
        }
        var shown = lines.Take(MaxLines).Select(l => l.Length > MaxLineLength ? l[..MaxLineLength] + "…" : l);
        return $"\"{string.Join(" / ", shown)}{(lines.Count > MaxLines ? " / …" : "")}\"";
    }

    private static string OneLine(string text) => ControlCharacters().Replace(AnsiEscapes().Replace(text, ""), " ").Trim();

    private static string Seconds(TimeSpan time) => string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.0} s");

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapes();

    [GeneratedRegex(@"[\p{Cc}\s]+")]
    private static partial Regex ControlCharacters();

    /// <summary>One output stream, read to its end, keeping the first <see cref="KeptChars"/> characters.</summary>
    private sealed class Capture
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _gate = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        public async Task ReadAsync(StreamReader reader)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (_gate)
                    {
                        if (_text.Length < KeptChars)
                        {
                            _text.Append(buffer, 0, Math.Min(read, KeptChars - _text.Length));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The process was stopped or disposed: what was read so far is what it printed.
            }
        }
    }
}
