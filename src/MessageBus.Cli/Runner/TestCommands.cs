using System.Text.Json;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Runner;

/// <summary>
/// The repository's test commands, for a role that may run tests but not other commands (the original issue, a reviewer that
/// checks the tests pass). Only commands from a fixed list of test runners are ever allowed; the working copy decides
/// which of them apply: its project files (a solution, <c>package.json</c> with a <c>test</c> script, <c>Cargo.toml</c> …)
/// and the commands its team workflow file (<c>.messagebus/workflow.md</c>) and CI workflows name. A repository can't
/// widen the list by naming some other command there.
/// </summary>
public static class TestCommands
{
    /// <summary>How deep below the working copy's root project files are looked for (<c>src/App/App.csproj</c>).</summary>
    private const int ProjectDepth = 3;

    private static readonly string[] Runners =
    [
        "dotnet test", "npm test", "npm run test", "pnpm test", "pnpm run test", "yarn test", "yarn run test", "npx vitest",
        "cargo test", "go test", "pytest", "python -m pytest", "python3 -m pytest", "mvn test", "./mvnw test", "gradle test", "./gradlew test",
    ];

    private static readonly string[] SkippedFolders = ["node_modules", ".git", "bin", "obj", "target", "dist", "build", "vendor", ".venv"];

    /// <summary>
    /// <paramref name="profile"/> with a Claude Code rule (<c>Bash(dotnet test:*)</c>) for each of the repository's test
    /// commands, when the role may run tests but not other commands; otherwise <paramref name="profile"/> as it is.
    /// </summary>
    /// <param name="profile">The run's permission profile.</param>
    /// <param name="workTree">The run's working copy.</param>
    public static PermissionProfile Apply(PermissionProfile profile, string workTree)
    {
        if (!profile.CanRunTests || profile.CanRunCommands)
        {
            return profile;
        }
        var rules = Detect(workTree).Select(command => $"Bash({command}:*)").ToList();
        return rules.Count == 0
            ? profile
            : profile with { AllowedTools = [.. profile.AllowedTools.Concat(rules).Distinct(StringComparer.Ordinal)] };
    }

    /// <summary>The test commands that apply to the repository in <paramref name="workTree"/>, from <see cref="Runners"/> only.</summary>
    public static IReadOnlyList<string> Detect(string workTree)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(workTree))
        {
            return [];
        }
        foreach (var file in ProjectFiles(workTree))
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file);
            if (extension is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj")
            {
                found.Add("dotnet test");
            }
            else if (name == "package.json" && HasTestScript(file))
            {
                var folder = Path.GetDirectoryName(file)!;
                found.Add(File.Exists(Path.Combine(folder, "pnpm-lock.yaml")) ? "pnpm test"
                    : File.Exists(Path.Combine(folder, "yarn.lock")) ? "yarn test"
                    : "npm test");
            }
            else if (name == "Cargo.toml")
            {
                found.Add("cargo test");
            }
            else if (name == "go.mod")
            {
                found.Add("go test");
            }
            else if (name is "pytest.ini" or "conftest.py" || (name == "pyproject.toml" && Contains(file, "[tool.pytest")))
            {
                found.Add("pytest");
                found.Add("python -m pytest");
            }
            else if (name == "pom.xml")
            {
                found.Add("mvn test");
            }
            else if (name is "build.gradle" or "build.gradle.kts")
            {
                found.Add("gradle test");
            }
        }
        foreach (var file in WorkflowFiles(workTree))
        {
            foreach (var line in ReadLines(file))
            {
                foreach (var segment in line.Split(["&&", "||", ";", "|"], StringSplitOptions.TrimEntries))
                {
                    var command = segment.TrimStart('-', ' ', '$', '>').Trim();
                    if (command.StartsWith("run:", StringComparison.Ordinal))
                    {
                        command = command[4..].Trim();
                    }
                    if (Runners.FirstOrDefault(r => command == r || command.StartsWith(r + " ", StringComparison.Ordinal)) is { } runner)
                    {
                        found.Add(runner);
                    }
                }
            }
        }
        return [.. Runners.Where(found.Contains)];
    }

    /// <summary>A .NET marker at the root or one level below, without following directory links.</summary>
    public static bool IsDotnetRepository(string root) => ProjectFiles(root, maxDepth: 1).Any(file =>
        Path.GetFileName(file).Equals("global.json", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(file).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj");

    private static IEnumerable<string> ProjectFiles(string root, int maxDepth = ProjectDepth)
    {
        var folders = new List<(string Path, int Depth)> { (root, 0) };
        for (var i = 0; i < folders.Count; i++)
        {
            var (folder, depth) = folders[i];
            string[] files, children;
            try
            {
                files = Directory.GetFiles(folder);
                children = depth < maxDepth ? Directory.GetDirectories(folder) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                yield return file;
            }
            foreach (var child in children)
            {
                if (SkippedFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                        folders.Add((child, depth + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A child may disappear or become unreadable after listing its parent; skip it.
                }
            }
        }
    }

    private static IEnumerable<string> WorkflowFiles(string root)
    {
        var team = Path.Combine(root, ".messagebus", "workflow.md");
        if (File.Exists(team))
        {
            yield return team;
        }
        var ci = Path.Combine(root, ".github", "workflows");
        if (Directory.Exists(ci))
        {
            foreach (var file in Directory.EnumerateFiles(ci).Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            {
                yield return file;
            }
        }
        var azure = Path.Combine(root, "azure-pipelines.yml");
        if (File.Exists(azure))
        {
            yield return azure;
        }
    }

    private static bool HasTestScript(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.String;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool Contains(string file, string text) => ReadLines(file).Any(l => l.Contains(text, StringComparison.Ordinal));

    private static IEnumerable<string> ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
