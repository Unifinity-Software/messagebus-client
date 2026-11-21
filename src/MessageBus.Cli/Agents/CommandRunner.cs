using System.ComponentModel;
using System.Diagnostics;

namespace MessageBus.Cli.Agents;

/// <summary>What a short command printed.</summary>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
internal sealed record CommandResult(int ExitCode, string Output, string Error)
{
    /// <summary>Both streams, for checks that don't care which one a tool writes to.</summary>
    public string AllOutput => Output + "\n" + Error;
}

/// <summary>Runs a short command (an agent's <c>--version</c>, a tool's version, a sign-in status) and captures its output.</summary>
internal static class CommandRunner
{
    /// <summary>How long a version check may take before it is given up on.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Exit code and output, or null when it couldn't start or didn't finish in time.</summary>
    /// <param name="executable">The program.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <param name="timeout">How long it may take (default <see cref="Timeout"/>).</param>
    /// <param name="commandLine">Raw arguments for cmd.exe, whose shell quoting differs from executable argv; otherwise use args.</param>
    /// <param name="environment">Variables to add to what it inherits (the runner service's stored agent sign-ins, the original issue).</param>
    public static async Task<CommandResult?> RunAsync(string executable, IReadOnlyList<string> args, CancellationToken cancellationToken, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? environment = null, string? commandLine = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (commandLine is not null) start.Arguments = commandLine;
        else foreach (var arg in args) start.ArgumentList.Add(arg);
        // Agents that look for a terminal shouldn't wait for one.
        start.Environment["CI"] = "1";
        start.Environment["NO_COLOR"] = "1";
        CodexAuthentication.RemoveKeys(start.Environment);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            if (!CodexAuthentication.IsKeyVariable(name)) start.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new Win32Exception("not started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }

        using (process)
        using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout ?? Timeout);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                return new CommandResult(process.ExitCode, output, error);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }
    }
}
