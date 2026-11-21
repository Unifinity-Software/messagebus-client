using System.IO.Compression;
using System.Security.Cryptography;
using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class DotnetToolInstallTests
{
    internal static DotnetToolInstall Install(string tools, string version = "1.0.0") => new(tools, false)
    {
        Package = new(DotnetToolInstall.PackageId, version, Path.Combine(tools, ".store", "messagebus.ai.cli", version)),
    };

    internal static string WriteRecoverableTool(DotnetToolInstall install, string program, bool manifestChecksum = false)
    {
        var package = install.Package!;
        var directory = Path.Combine(package.StoreEntry, "messagebus.ai.cli", package.Version);
        const string entryPoint = "tools/net10.0/any/mbus.dll";
        var entry = Path.Combine(directory, entryPoint);
        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
        File.WriteAllText(entry, program);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(entry)!, DotnetToolInstall.SettingsFile), "<DotNetCliTool/>");
        var packagePath = Path.Combine(directory, $"messagebus.ai.cli.{package.Version}.nupkg");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(entry, entryPoint);
            using var manifest = new StreamWriter(archive.CreateEntry("MessageBus.Ai.Cli.nuspec").Open());
            manifest.Write($"<package><metadata><id>{package.Id}</id><version>{package.Version}</version></metadata></package>");
        }
        if (manifestChecksum) File.Copy(packagePath, Path.Combine(directory, package.Id + ".nupkg"));
        using var reader = ZipFile.OpenRead(packagePath);
        using var checksummed = manifestChecksum ? reader.GetEntry("MessageBus.Ai.Cli.nuspec")!.Open() : File.OpenRead(packagePath);
        File.WriteAllText(packagePath + ".sha512", Convert.ToBase64String(SHA512.HashData(checksummed)));
        return entry;
    }

    [Theory]
    [InlineData("0.1.1-preview.1", null, " --prerelease")]
    [InlineData("0.1.1-preview.1+build.2", null, " --prerelease")]
    [InlineData("0.1.1", null, "")]
    [InlineData("0.1.1+build-with-hyphens", null, "")]
    [InlineData("0.1.1-preview.1", "0.2.0-preview.1", " --version 0.2.0-preview.1")]
    [InlineData("0.1.1", "0.2.0", " --version 0.2.0")]
    public void Updates_follow_the_running_channel_unless_a_version_is_pinned(string running, string? pinned, string suffix)
    {
        Assert.Equal($"dotnet tool update -g MessageBus.Ai.Cli{suffix}",
            new DotnetToolInstall("tools", true).UpdateCommand(pinned, running));
        Assert.Equal($"dotnet tool update --tool-path \"my tools\" MessageBus.Ai.Cli{suffix}",
            new DotnetToolInstall("my tools", false).UpdateCommand(pinned, running));
        Assert.Equal($"dotnet tool update MessageBus.Ai.Cli{suffix}",
            new DotnetToolInstall(null, false).UpdateCommand(pinned, running));
    }
}
