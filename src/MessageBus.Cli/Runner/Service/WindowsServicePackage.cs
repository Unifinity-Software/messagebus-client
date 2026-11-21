using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Update;

namespace MessageBus.Cli.Runner.Service;

[SupportedOSPlatform("windows")]
internal static class WindowsServicePackage
{
    internal const string Source = DotnetToolUpdater.NuGetOrg;

    internal static string PackageStore(string tools) => Path.Combine(tools, ".store", DotnetToolInstall.PackageId.ToLowerInvariant());

    internal static string FindWorkerAssembly(string tools, string assemblyName) => FindAssembly(PackageStore(tools), assemblyName);

    private static string FindAssembly(string folder, string assemblyName = "mbus.dll")
    {
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, assemblyName, SearchOption.AllDirectories) : [];
        return files.Length == 1 ? files[0] : throw new CliException($"The service install needs exactly one {assemblyName} in {folder}.");
    }

    internal static async Task<string> StageHostAsync(WindowsServicePaths paths, string version, CliHost host, CancellationToken ct)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(paths.Root, "install")).FullName;
        var source = Directory.CreateDirectory(paths.HostPackageSource).FullName;
        var packageId = DotnetToolInstall.PackageId.ToLowerInvariant();
        var packageVersion = version.ToLowerInvariant();
        var packageName = $"{packageId}.{packageVersion}.nupkg";
        var package = Path.Combine(source, packageName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4), host.Time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            // The elevated process only downloads and verifies the archive. Tool installation and
            // execution belong to the service identity, which has its own NuGet home.
            using var http = host.CreateReleaseHttpClient();
            using var index = JsonDocument.Parse(await http.GetStringAsync(Source, stop.Token));
            var address = index.RootElement.GetProperty("resources").EnumerateArray()
                .First(resource => resource.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0").GetProperty("@id").GetString();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var feed)
                || feed.Scheme != Uri.UriSchemeHttps || !feed.Host.Equals("api.nuget.org", StringComparison.OrdinalIgnoreCase)
                || !feed.IsDefaultPort || feed.UserInfo.Length != 0 || feed.Query.Length != 0 || feed.Fragment.Length != 0)
                throw new CliException("The NuGet service did not return a trusted package download URL.");
            using (var response = await http.GetAsync(new Uri(feed, $"{packageId}/{packageVersion}/{packageName}"),
                HttpCompletionOption.ResponseHeadersRead, stop.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var target = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(target, stop.Token);
            }
            var config = Path.Combine(scratch, "NuGet.Config");
            await File.WriteAllTextAsync(config, DotnetToolUpdater.NuGetConfig(Source), stop.Token);
            var verify = paths.ProcessStart(paths.Dotnet, scratch, scratch, worker: false);
            foreach (var arg in new[] { "nuget", "verify", package, "--all", "--configfile", config })
                verify.ArgumentList.Add(arg);
            if (await ServiceChild.RunAsync(verify, sendStop: false, host.Time, stop.Token) != 0)
                throw new CliException($"NuGet could not verify the signature of {DotnetToolInstall.PackageId} {version}.");
            var payload = Path.Combine(paths.HostTools, "payload");
            ZipFile.ExtractToDirectory(package, payload);
            var assembly = FindAssembly(Path.Combine(payload, "tools"));
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(assembly)!, DotnetToolInstall.SettingsFile)))
                throw new CliException("The signed package does not contain the expected .NET tool payload.");
            Directory.Delete(scratch, recursive: true);
            return assembly;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
            || ex is OperationCanceledException && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CliException($"Could not download or verify {DotnetToolInstall.PackageId} {version} from NuGet.org. Check connectivity and the .NET SDK.");
        }
    }

    internal static async Task InstallWorkerAsync(WindowsServicePaths paths, string version, TimeProvider time, CancellationToken ct)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(paths.State, "bootstrap")).FullName;
        var file = Path.Combine(scratch, "NuGet.Config");
        await File.WriteAllTextAsync(file, DotnetToolUpdater.NuGetConfig(paths.HostPackageSource), ct);
        var start = paths.ProcessStart(paths.Dotnet, scratch, paths.Home, worker: true);
        foreach (var arg in new[] { "tool", "install", DotnetToolInstall.PackageId, "--tool-path", paths.WorkerTools,
            "--version", version, "--configfile", file, "--no-cache" })
            start.ArgumentList.Add(arg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var exit = await ServiceChild.RunAsync(start, sendStop: false, time, stop.Token);
            if (exit != 0) throw new CliException($"Installing the private worker from {DotnetToolInstall.PackageId} {version} failed (dotnet exit {exit}).");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CliException("Installing the Windows runner package timed out.");
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class ServiceChild
{
    internal static async Task<int> RunAsync(ProcessStartInfo start, bool sendStop, TimeProvider time, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var job = Sandbox.WindowsJob.Create();
        start.RedirectStandardInput = true;
        using var process = Process.Start(start) ?? throw new CliException("The Windows runner child could not be started.");
        try
        {
            try { job.Add(process.Id); }
            catch (Win32Exception) when (!sendStop && process.HasExited) { return process.ExitCode; }
            if (sendStop)
            {
                // The worker waits for this byte before starting. Every process it starts is in this job.
                await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1 }, ct);
                await process.StandardInput.BaseStream.FlushAsync(ct);
            }
            else process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && sendStop)
        {
            try { process.StandardInput.Close(); } // EOF is the worker's equivalent of Ctrl+C.
            catch (IOException) { }
            try { await process.WaitForExitAsync().WaitAsync(RunnerServiceCommand.StopWait, time); }
            catch (TimeoutException) { job.Terminate(); }
            throw;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
