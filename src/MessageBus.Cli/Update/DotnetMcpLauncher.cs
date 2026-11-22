using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Mcp;
using MessageBus.Cli.Runner.Sandbox;

namespace MessageBus.Cli.Update;

/// <summary>
/// Agent-owned MCP sessions must not load the dotnet tool's shim or store: Windows cannot update them while loaded.
/// Connect installs an immutable launcher outside the store. It copies the currently installed program for each
/// version and holds a lease until that session exits, including assemblies first loaded after a tool update.
/// </summary>
internal static class DotnetMcpLauncher
{
    internal const string Command = "__mcp-launch";
    internal const string FolderName = ".mbus-mcp";
    private const string LeaseName = ".lease";
    private const string ReadyName = ".ready";

    internal sealed record Connection(McpLaunchCommand Launch, string Directory, Action Install);

    internal static Connection? ForConnection(CliHost host)
    {
        if (host.ImageManaged || DotnetToolInstall.Running is not { ToolsPath: not null, Package: not null } tool)
            return null;
        var dotnet = ExecutableLocator.Find(host, "dotnet")?.Path
            ?? throw new CliException("The dotnet host could not be found for the mbus MCP launcher.");
        return ForConnection(tool, Assembly.GetEntryAssembly()!.Location, dotnet);
    }

    internal static Connection ForConnection(DotnetToolInstall tool, string entry, string dotnet)
    {
        var root = Root(tool.ToolsPath!);
        // Never replace this bootstrap while an agent has it loaded. Its v1 contract only resolves/copies the
        // installed package; MCP and all changing product behavior run in the current package's runtime copy.
        var launcher = Path.Combine(root, "launcher-v1");
        return new(new(dotnet, [Path.Combine(launcher, "mbus.dll"), Command, tool.ToolsPath!, "mcp"]), launcher,
            () => CopyProgram(Path.GetDirectoryName(entry)!, launcher));
    }

    internal static async Task<int> RunAsync(string tools)
    {
        try
        {
            var dotnet = Environment.ProcessPath!; // The agent starts the retained launcher with the absolute dotnet host.
            using var runtime = Acquire(tools);
            CleanUp(tools, runtime.Directory);
            var start = new ProcessStartInfo(dotnet) { UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(runtime.Directory, "mbus.dll"));
            start.ArgumentList.Add("mcp");
            // No pipe proxy or shell: the child inherits exactly the agent's stdin/stdout and environment.
            using var job = OperatingSystem.IsWindows() ? CreateJob() : null;
            using var child = Process.Start(start) ?? throw new IOException("The mbus MCP process could not start.");
            if (OperatingSystem.IsWindows())
            {
                try { job?.Add(child.Id); }
                catch (Win32Exception) { }
            }
            ConsoleCancelEventHandler cancel = (_, e) => e.Cancel = true; // The child receives console signals too.
            Console.CancelKeyPress += cancel;
            using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                _ = Kill(child.Id, 15);
            });
            try { await child.WaitForExitAsync(); }
            finally { Console.CancelKeyPress -= cancel; }
            return child.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            await Console.Error.WriteLineAsync("Couldn't start mbus MCP: " + ex.Message);
            return 1;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WindowsJob? CreateJob()
    {
        try { return WindowsJob.Create(); }
        catch (Win32Exception) { return null; } // A containing job may forbid nesting.
    }

    internal sealed class RuntimeLease(string directory, FileStream lease) : IDisposable
    {
        internal string Directory { get; } = directory;
        public void Dispose() => lease.Dispose();
    }

    internal static RuntimeLease? LeaseCurrentRuntime(string? programDirectory = null)
    {
        var directory = Path.TrimEndingDirectorySeparator(programDirectory ?? AppContext.BaseDirectory);
        var parent = Path.GetDirectoryName(directory);
        if (parent is null || Path.GetFileName(parent) != "runtimes"
            || Path.GetFileName(Path.GetDirectoryName(parent)) != FolderName)
            return null;
        // The MCP child also holds its own lease: on Unix it may outlive a bootstrap killed with SIGKILL.
        return new(directory, OpenLease(directory));
    }

    private static FileStream OpenLease(string directory) =>
        new(Path.Combine(directory, LeaseName), FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static RuntimeLease Acquire(string tools)
    {
        var version = DotnetToolPackage.InstalledVersion(tools, DotnetToolInstall.PackageId)
            ?? throw new IOException("The installed mbus package is unavailable. If an update is in progress, reconnect MCP after it finishes.");
        var store = Path.Combine(DotnetToolPackage.StorePath(tools, DotnetToolInstall.PackageId), version);
        var settings = Directory.GetFiles(store, DotnetToolInstall.SettingsFile, SearchOption.AllDirectories).Single();
        var source = Path.GetDirectoryName(settings)!;
        var checksum = Directory.GetFiles(store, "*.nupkg.sha512", SearchOption.AllDirectories).Single();
        var identity = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(checksum))));
        var directory = Path.Combine(Root(tools), "runtimes", version + "-" + identity[..16]);
        // An inactive copy may be retired between publication and opening its lease. Retry only that race.
        for (var attempt = 0; ; attempt++)
        {
            CopyProgram(source, directory);
            try
            {
                return new(directory, OpenLease(directory));
            }
            catch (IOException) when (attempt < 2) { }
        }
    }

    internal static void CleanUp(string tools, string keep)
    {
        var versions = Path.Combine(Root(tools), "runtimes");
        if (!Directory.Exists(versions)) return;
        foreach (var directory in Directory.EnumerateDirectories(versions))
        {
            if (DotnetToolInstall.SamePath(directory, keep) || !File.Exists(Path.Combine(directory, ReadyName))) continue;
            try
            {
                // FileShare.Delete permits retirement while this exclusive lease prevents a new reader. Active
                // launchers hold shared read leases, so their complete dependency trees stay untouched.
                using var lease = new FileStream(Path.Combine(directory, LeaseName), FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Root(string tools) => Path.Combine(Path.GetFullPath(tools), FolderName);

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    private static void CopyProgram(string source, string directory)
    {
        if (File.Exists(Path.Combine(directory, ReadyName))) return;
        var staging = directory + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            if (!File.Exists(Path.Combine(staging, "mbus.dll")) || !File.Exists(Path.Combine(staging, "mbus.runtimeconfig.json")))
                throw new IOException("The installed mbus program is incomplete.");
            File.WriteAllText(Path.Combine(staging, LeaseName), "");
            File.WriteAllText(Path.Combine(staging, ReadyName), "");
            try { Directory.Move(staging, directory); }
            catch (IOException) when (File.Exists(Path.Combine(directory, ReadyName))) { } // Another session published it first.
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
