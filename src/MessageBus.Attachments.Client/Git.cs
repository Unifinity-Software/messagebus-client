using System.Diagnostics;
using System.Text;

namespace MessageBus.Attachments.Client;

/// <summary>A git command that failed: its arguments, exit code and what it printed on stderr.</summary>
public sealed class GitException(string message, bool rawOutput = false, string? input = null, string? standardError = null) : Exception(message)
{
    public string? StandardError { get; } = standardError;
    public bool RawOutput { get; } = rawOutput;
    public string? Input { get; } = input;
}

/// <summary>Runs the <c>git</c> command line (on the PATH, or <c>MBUS_GIT</c>) and returns its standard output.</summary>
public static class Git
{
    private static readonly AsyncLocal<Action<ProcessStartInfo>?> Preparer = new();

    /// <summary>
    /// Every git started from this async flow (until disposed) goes through <paramref name="prepare"/> first: the runner
    /// starts git for a task's mbus tools inside that task's sandbox and environment (the original issue). Null leaves git as it is.
    /// </summary>
    public static IDisposable PrepareWith(Action<ProcessStartInfo>? prepare)
    {
        var previous = Preparer.Value;
        Preparer.Value = prepare;
        return new Restore(previous);
    }

    private sealed class Restore(Action<ProcessStartInfo>? previous) : IDisposable
    {
        public void Dispose() => Preparer.Value = previous;
    }

    /// <summary>Runs <c>git -C <paramref name="directory"/> args…</c>; throws <see cref="GitException"/> on a non-zero exit.</summary>
    /// <param name="directory">The working directory (a repository, or any folder for commands that need none).</param>
    /// <param name="args">The arguments, passed as they are (no shell).</param>
    /// <param name="ct">Kills git when cancelled.</param>
    public static async Task<string> RunAsync(string directory, IEnumerable<string> args, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MBUS_GIT") is { Length: > 0 } git ? git : "git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Git gets a stdin of its own. Under `mbus mcp` our stdin is the MCP pipe the server is always reading,
            // and on Windows a child that inherits a pipe handle with a read pending on it can block at start-up
            // until the next MCP message arrives, which never comes while the agent waits for this call (the original issue).
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var argList = args.ToList();
        foreach (var arg in argList) start.ArgumentList.Add(arg);
        // Never stop to ask for credentials or an editor.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["LC_ALL"] = "C";
        start.Environment["LANGUAGE"] = "C";
        Preparer.Value?.Invoke(start);

        using var process = Start(start);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
            {
            }
            // Kill requests termination; disposal does not wait. Do not let a cancelled git
            // keep writing (or holding index.lock) after its caller has moved on.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            // An escaped child can keep the redirected pipes open after git exits.
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new GitException($"git {string.Join(' ', argList)} failed (exit {process.ExitCode}): {error.Trim()}", rawOutput: true, standardError: error);
        return output;
    }

    private static Process Start(ProcessStartInfo start)
    {
        if (!Directory.Exists(start.WorkingDirectory)) throw new GitException("That folder doesn't exist.", input: "path");
        try { return Process.Start(start) ?? throw new GitException("Couldn't start git. Is it installed and on the PATH?"); }
        catch (System.ComponentModel.Win32Exception) { throw new GitException("Couldn't start git. Is it installed and on the PATH?"); }
    }

    /// <summary>The commit id <paramref name="revision"/> names in <paramref name="repository"/>.</summary>
    /// <param name="repository">The repository.</param>
    /// <param name="revision">A branch, tag, commit or expression.</param>
    /// <param name="ct">Cancels.</param>
    public static async Task<string> CommitIdAsync(string repository, string revision, CancellationToken ct = default) =>
        (await RunAsync(repository, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], ct)).Trim();
}

/// <summary>New, empty temporary folders under a given root (or the system's temp folder).</summary>
public static class TempFolders
{
    /// <summary>A new folder named <paramref name="prefix"/> plus a random part, under <paramref name="root"/> (the system's temp folder when null).</summary>
    public static DirectoryInfo Create(string? root, string prefix) =>
        root is null ? Directory.CreateTempSubdirectory(prefix) : Directory.CreateDirectory(Path.Combine(root, prefix + Guid.NewGuid().ToString("N")[..12]));
}
