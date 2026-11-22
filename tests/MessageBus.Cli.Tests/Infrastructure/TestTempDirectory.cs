using System.Runtime.CompilerServices;

namespace MessageBus.Cli.Tests.Infrastructure;

internal static class TestTempDirectory
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsMacOS())
        {
            // Only this test process and its children: profiles intentionally reject directory aliases,
            // and npm requires its working directory and explicit prefix to name the same physical path.
            Environment.SetEnvironmentVariable("TMPDIR", PhysicalDirectory(Path.GetTempPath()), EnvironmentVariableTarget.Process);
        }
    }

    internal static string PhysicalDirectory(string directory)
    {
        var path = Path.GetFullPath(directory);
        for (var links = 0; links < 64; links++)
        {
            var root = Path.GetPathRoot(path)!;
            var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            var redirected = false;
            for (var i = 0; i < parts.Length; i++)
            {
                var next = new DirectoryInfo(Path.Combine(current, parts[i]));
                if (next.ResolveLinkTarget(returnFinalTarget: false) is not { } target)
                {
                    current = next.FullName;
                    continue;
                }
                // Resolve target ancestors too: the target itself may be inside another alias.
                path = target.FullName;
                for (var j = i + 1; j < parts.Length; j++) path = Path.Combine(path, parts[j]);
                redirected = true;
                break;
            }
            if (!redirected) return Path.TrimEndingDirectorySeparator(current);
        }
        throw new IOException("The test temporary directory contains too many directory aliases.");
    }
}
