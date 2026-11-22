using System.Diagnostics;
using MessageBus.Cli.Configuration;
using Xunit;

namespace MessageBus.Cli.Tests.Infrastructure;

public sealed class TestTempDirectoryTests
{
    [Fact]
    public async Task Canonical_fixture_paths_resolve_ancestor_links_without_allowing_profile_aliases()
    {
        var root = Directory.CreateDirectory(Path.Combine(TestTempDirectory.PhysicalDirectory(Path.GetTempPath()),
            "mbus-physical-temp-" + Guid.NewGuid().ToString("N"))).FullName;
        var ancestor = Path.Combine(root, "ancestor");
        var indirect = Path.Combine(root, "indirect");
        try
        {
            var physical = Directory.CreateDirectory(Path.Combine(root, "physical", "nested", "work")).FullName;
            await LinkAsync(ancestor, Path.Combine(root, "physical"));
            await LinkAsync(indirect, Path.Combine(ancestor, "nested"));
            var resolved = TestTempDirectory.PhysicalDirectory(Path.Combine(indirect, "work"));
            Assert.Equal(physical, resolved, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var profile = Path.Combine(resolved, "profile");
            _ = new ConfigStore(profile, "file");
            var files = Directory.GetFiles(profile).Order(StringComparer.Ordinal).ToArray();
            Assert.Throws<IOException>(() => new ConfigStore(Path.Combine(indirect, "work", "profile"), "file"));
            Assert.Equal(files, Directory.GetFiles(profile).Order(StringComparer.Ordinal));
        }
        finally
        {
            foreach (var alias in new[] { indirect, ancestor })
                if (new DirectoryInfo(alias).LinkTarget is not null) Directory.Delete(alias);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task LinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        // Directory junctions exercise the Windows alias refusal without requiring symlink privileges.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
    }
}
