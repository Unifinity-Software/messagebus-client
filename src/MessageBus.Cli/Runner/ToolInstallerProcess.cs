using System.Diagnostics;

namespace MessageBus.Cli.Runner;

/// <summary>Runs only a platform's fixed installer argument vector, with bounded lifetime and pipe handling.</summary>
internal static class ToolInstallerProcess
{
    public static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan limit, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new CliException("The approved installer could not start.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { /* The OS owns an elevated child, so keep the durable pause. */ }
            throw new CliException(ToolSetupRequests.InterruptedDetail);
        }
    }
}
