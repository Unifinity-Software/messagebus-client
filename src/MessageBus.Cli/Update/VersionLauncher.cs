using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using MessageBus.Cli.Runner.Sandbox;

namespace MessageBus.Cli.Update;

/// <summary>
/// Windows (the original issue): runs a long-running mbus command from the current version's own file (<see cref="VersionedInstall.HandOff"/>).
/// This process only waits: the child shares its console and standard input and output, so an agent talking MCP to
/// <c>mbus.exe mcp</c> talks to the child, and its exit code becomes this one's. Both are in one job that ends with this
/// process, so an agent or Task Scheduler ending <c>mbus.exe</c> ends the child too.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class VersionLauncher
{
    /// <summary>Starts <paramref name="file"/> with <paramref name="args"/> and returns its exit code; null when it couldn't be started.</summary>
    /// <param name="file">The current version's file.</param>
    /// <param name="args">The command line.</param>
    public static int? Run(string file, string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        WindowsJob? job = null;
        try
        {
            job = WindowsJob.Create();
        }
        catch (Win32Exception)
        {
            // No job (a parent's job forbids it): the child still runs, it just may outlive a killed parent.
        }
        Process? child;
        try
        {
            child = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            child = null;
        }
        if (child is null)
        {
            job?.Dispose();
            return null;
        }
        using (job)
        using (child)
        {
            try
            {
                job?.Add(child.Id);
            }
            catch (Win32Exception)
            {
            }
            // Ctrl+C reaches the child too (same console): it decides what happens, this process just waits for it.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            child.WaitForExit();
            return child.ExitCode;
        }
    }
}
