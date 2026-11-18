namespace MessageBus.Cli.Connect;

/// <summary>One file of a skill: its path inside the skill folder (forward slashes) and its bytes.</summary>
public sealed record SkillFile(string RelativePath, byte[] Content);

/// <summary>Where <c>mbus connect</c> gets the orchestrate skill (the original issue) it installs into each agent.</summary>
public interface ISkillSource
{
    /// <summary>The skill's folder name, as each agent will show it.</summary>
    string Name { get; }

    /// <summary>The skill's files; empty when this build of mbus has none.</summary>
    IReadOnlyList<SkillFile> GetFiles();
}

/// <summary>
/// The skill built into mbus from <c>skills/mbus-orchestrate/**</c> (see the project file), so a
/// single-file mbus on another machine carries it.
/// </summary>
internal sealed class EmbeddedSkillSource : ISkillSource
{
    private const string Prefix = "skills/mbus-orchestrate/";

    public string Name => "mbus-orchestrate";

    public IReadOnlyList<SkillFile> GetFiles()
    {
        var assembly = typeof(EmbeddedSkillSource).Assembly;
        var files = new List<SkillFile>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // %(RecursiveDir) uses the build machine's separator.
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files.Add(new SkillFile(normalized[Prefix.Length..], copy.ToArray()));
        }
        return files;
    }
}
